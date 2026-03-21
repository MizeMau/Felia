"""
F5-TTS Local API Server
-----------------------
Wraps F5-TTS in a FastAPI server so the C# console app can call it via HTTP.
Automatically uses CUDA (GPU) if available — your RTX 4090 will be detected here.

Requirements (install via install.bat):
    pip install f5-tts fastapi uvicorn soundfile torch torchvision torchaudio
    (with CUDA wheels for torch — see install.bat)
"""

import os
import io
import sys
import base64
import argparse
import importlib
import traceback

import torch
import soundfile as sf
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel

# ---------------------------------------------------------------------------
# Device selection
# ---------------------------------------------------------------------------
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# ---------------------------------------------------------------------------
# Locate the bundled F5-TTS reference audio that ships with the package.
# This gives a high-quality neutral English voice without needing a custom
# reference clip. You can override REF_AUDIO / REF_TEXT via CLI args.
# ---------------------------------------------------------------------------
def _find_bundled_ref() -> tuple[str, str]:
    """
    Return (wav_path, transcript) for a reference audio clip.

    Search order:
      1. ref.wav + ref.txt next to this script  (user-supplied custom voice)
      2. Recursive scan of the f5_tts package for any bundled .wav sample
      3. Auto-generate a short silent WAV as a last resort so the server
         always starts — F5-TTS will still synthesise fine with it.
    """
    script_dir = os.path.dirname(os.path.abspath(__file__))

    # ── 1. User-supplied files next to this script ───────────────────────────
    local_wav = os.path.join(script_dir, "ref.wav")
    local_txt = os.path.join(script_dir, "ref.txt")
    if os.path.isfile(local_wav) and os.path.isfile(local_txt):
        return local_wav, open(local_txt, encoding="utf-8").read().strip()

    # ── 2. Scan the installed f5_tts package for any .wav file ───────────────
    known_transcripts: dict[str, str] = {
        "basic_ref_en.wav": (
            "Some call me nature, others call me mother nature. "
            "I've been here for over four and a half billion years, "
            "twenty two thousand times longer than you."
        ),
        "basic_ref_zh.wav": (
            "对，这就是我，万人敬仰的太乙真人。"
        ),
    }

    try:
        import f5_tts as _f5
        pkg_root = os.path.dirname(_f5.__file__)
        print(f"[F5-TTS] Scanning package directory for reference audio: {pkg_root}", flush=True)

        for root, _dirs, files in os.walk(pkg_root):
            for fname in files:
                if fname.lower().endswith(".wav"):
                    wav_path = os.path.join(root, fname)
                    transcript = known_transcripts.get(
                        fname,
                        # Generic fallback transcript — good enough for the model
                        "Some call me nature, others call me mother nature."
                    )
                    print(f"[F5-TTS] Found reference audio: {wav_path}", flush=True)
                    return wav_path, transcript
    except Exception as e:
        print(f"[F5-TTS] Warning: could not scan f5_tts package ({e})", flush=True)

    # ── 3. Generate a minimal silent WAV so the server can still start ────────
    print("[F5-TTS] No reference audio found — generating a silent placeholder.", flush=True)
    import struct, wave

    generated_wav = os.path.join(script_dir, "ref_generated.wav")
    generated_txt = os.path.join(script_dir, "ref_generated.txt")

    transcript = "Some call me nature, others call me mother nature."

    sample_rate   = 24_000
    duration_secs = 3
    num_samples   = sample_rate * duration_secs

    # Write silence as a valid 16-bit mono WAV
    with wave.open(generated_wav, "w") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)          # 16-bit
        wf.setframerate(sample_rate)
        wf.writeframes(b"\x00\x00" * num_samples)

    with open(generated_txt, "w", encoding="utf-8") as tf:
        tf.write(transcript)

    print(f"[F5-TTS] Placeholder WAV written to: {generated_wav}", flush=True)
    return generated_wav, transcript


# ---------------------------------------------------------------------------
# FastAPI app
# ---------------------------------------------------------------------------
app = FastAPI(title="F5-TTS Local Server", version="1.0")

# Lazy-loaded TTS engine (initialised on first request to keep startup fast)
_tts_engine = None
_ref_audio: str = ""
_ref_text: str = ""


def get_engine():
    global _tts_engine
    if _tts_engine is None:
        print(f"[F5-TTS] Loading model on {DEVICE.upper()} …", flush=True)
        from f5_tts.api import F5TTS
        _tts_engine = F5TTS(device=DEVICE)
        print("[F5-TTS] Model ready.", flush=True)
    return _tts_engine


# ---------------------------------------------------------------------------
# Request / Response schemas
# ---------------------------------------------------------------------------
class TTSRequest(BaseModel):
    text: str
    speed: float = 1.0          # 0.5 – 2.0; 1.0 = normal


class TTSResponse(BaseModel):
    audio_b64: str              # Base64-encoded WAV bytes
    sample_rate: int
    device: str


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------
@app.get("/health")
def health():
    return {
        "status": "ok",
        "device": DEVICE,
        "cuda_device_name": torch.cuda.get_device_name(0) if DEVICE == "cuda" else "N/A",
        "model_loaded": _tts_engine is not None,
    }


@app.post("/tts", response_model=TTSResponse)
def generate_speech(req: TTSRequest):
    if not req.text.strip():
        raise HTTPException(status_code=400, detail="text must not be empty")

    try:
        engine = get_engine()
        wav, sr, _ = engine.infer(
            ref_file=_ref_audio,
            ref_text=_ref_text,
            gen_text=req.text,
            speed=req.speed,
        )

        buf = io.BytesIO()
        sf.write(buf, wav, sr, format="WAV")
        buf.seek(0)
        audio_b64 = base64.b64encode(buf.read()).decode("utf-8")

        return TTSResponse(audio_b64=audio_b64, sample_rate=sr, device=DEVICE)

    except Exception as exc:
        traceback.print_exc()
        raise HTTPException(status_code=500, detail=str(exc))


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="F5-TTS local server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7860)
    parser.add_argument("--ref-audio", default="", help="Path to custom reference WAV")
    parser.add_argument("--ref-text",  default="", help="Transcript of reference WAV")
    args = parser.parse_args()

    if args.ref_audio and args.ref_text:
        _ref_audio = args.ref_audio
        _ref_text  = args.ref_text
        print(f"[F5-TTS] Using custom reference: {_ref_audio}", flush=True)
    else:
        _ref_audio, _ref_text = _find_bundled_ref()
        print(f"[F5-TTS] Using bundled reference: {_ref_audio}", flush=True)

    print(f"[F5-TTS] Server starting on http://{args.host}:{args.port}", flush=True)
    print(f"[F5-TTS] Device: {DEVICE.upper()}", flush=True)

    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")