@echo off
:: ============================================================
:: install.bat  —  Set up F5-TTS Python environment
:: Run this ONCE before launching the C# app.
:: Requires Python 3.10+ and a CUDA-capable GPU (tested on RTX 4090)
:: ============================================================

echo ============================================================
echo  F5-TTS Dependency Installer
echo ============================================================
echo.

:: Check Python is available
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] Python not found. Install Python 3.10 or 3.11 from https://python.org
    pause
    exit /b 1
)

echo [1/5] Upgrading pip ...
python -m pip install --upgrade pip

echo.
echo [2/5] Installing PyTorch with CUDA 12.1 support (for RTX 4090) ...
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121

echo.
echo [3/5] Installing F5-TTS ...
pip install f5-tts

echo.
echo [4/5] Installing server dependencies ...
pip install fastapi uvicorn soundfile

echo.
echo [5/5] Pinning compatible numpy / scipy / transformers for Python 3.10 ...
pip install "numpy==1.26.4"
pip install "scipy>=1.14.0"
pip install "transformers==4.44.2"

echo.
echo ============================================================
echo  Installation complete!
echo  You can now run the C# console app.
echo ============================================================
pause