using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Felia
{
    /// <summary>
    /// Wraps LLamaSharp for in-process, GPU-accelerated Llama 3 8B inference.
    /// No external server required — everything runs inside this process.
    /// </summary>
    public sealed class LlmService : IDisposable
    {
        // ── HuggingFace download config ──────────────────────────────────────────
        private const string ModelRepo = "bartowski/Meta-Llama-3-8B-Instruct-GGUF";
        private const string ModelFile = "Meta-Llama-3-8B-Instruct-Q4_K_M.gguf";
        private const string HuggingFaceUrl =
            $"https://huggingface.co/{ModelRepo}/resolve/main/{ModelFile}";

        // ── Runtime ──────────────────────────────────────────────────────────────
        private readonly LLamaWeights _weights;
        private readonly LLamaContext _context;
        private readonly ChatSession _session;

        // ── System prompt (mirrors the Python version) ───────────────────────────
        private const string SystemPrompt =
            "You are a playful, teasing but caring AI girlfriend. " +
            "You are confident, expressive and slightly chaotic. " +
            "Your name is Felia" +
            "Speak natural English.";

        /// <summary>
        /// Loads the model (and downloads it on first run).
        /// Call this once at startup — it may take a few seconds.
        /// </summary>
        public static async Task<LlmService> CreateAsync(
            string? modelDirectory = null,
            int gpuLayerCount = 99,          // 99 = put all layers on GPU
            uint contextSize = 4096,
            IProgress<double>? downloadProgress = null)
        {
            var modelDir = modelDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Felia", "models", "llm");

            Directory.CreateDirectory(modelDir);

            var modelPath = Path.Combine(modelDir, ModelFile);

            if (!File.Exists(modelPath))
                await DownloadModelAsync(modelPath, downloadProgress);

            return new LlmService(modelPath, gpuLayerCount, contextSize);
        }

        // ── Private constructor ──────────────────────────────────────────────────
        private LlmService(string modelPath, int gpuLayerCount, uint contextSize)
        {
            var parameters = new ModelParams(modelPath)
            {
                GpuLayerCount = gpuLayerCount,  // offload everything to your 4090
                ContextSize = contextSize,
            };

            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);

            var executor = new InteractiveExecutor(_context);
            _session = new ChatSession(executor);

            // Inject system prompt
            //_session.History.AddMessage(AuthorRole.System, SystemPrompt);
            _session.History.AddMessage(AuthorRole.User, "For this conversation, adopt this persona: " + SystemPrompt);
            _session.History.AddMessage(AuthorRole.Assistant, "Understood! I'm ready, let's talk~");
        }

        /// <summary>
        /// Send a user message and get the full reply back as a string.
        /// This runs synchronously on the calling thread (no streaming).
        /// </summary>
        public async Task<string> ChatAsync(string userMessage,
            CancellationToken cancellationToken = default)
        {
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 256,
                AntiPrompts = new List<string> { "User:", "Human:", "\nUser"},
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.8f,
                    TopP = 0.9f,
                    RepeatPenalty = 1.1f,
                },
            };

            var sb = new System.Text.StringBuilder();

            await foreach (var token in _session.ChatAsync(
                new ChatHistory.Message(AuthorRole.User, userMessage),
                inferenceParams,
                cancellationToken))
            {
                sb.Append(token);
            }

            var result = sb.ToString().Trim();

            // Safety net in case the model still echoes a role label
            if (result.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
                result = result["System:".Length..].TrimStart();
            if (result.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase))
                result = result["Assistant:".Length..].TrimStart();

            return result;
        }

        // ── Model downloader ─────────────────────────────────────────────────────
        private static async Task DownloadModelAsync(
            string targetPath,
            IProgress<double>? progress)
        {
            Console.WriteLine($"[LlmService] Model not found. Downloading from HuggingFace...");
            Console.WriteLine($"[LlmService] → {HuggingFaceUrl}");
            Console.WriteLine($"[LlmService] → Saving to: {targetPath}");

            var tempPath = targetPath + ".tmp";
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var resp = await http.GetAsync(HuggingFaceUrl,
                                       HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                var totalBytes = resp.Content.Headers.ContentLength ?? -1L;

                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dest = File.Create(tempPath);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;

                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;

                    if (totalBytes > 0)
                        progress?.Report((double)downloaded / totalBytes);

                    // Console progress (no external dependency)
                    if (totalBytes > 0)
                    {
                        var pct = downloaded * 100.0 / totalBytes;
                        var mb = downloaded / 1_048_576.0;
                        Console.Write($"\r[LlmService] {mb:F0} MB  ({pct:F1}%)   ");
                    }
                }

                Console.WriteLine("\n[LlmService] Download complete.");
            }
            File.Move(tempPath, targetPath, overwrite: true);
        }

        public void Dispose()
        {
            _context.Dispose();
            _weights.Dispose();
        }
    }
}