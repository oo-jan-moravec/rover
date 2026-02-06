sealed class AudioCaptureService : BackgroundService
{
    private readonly WebSocketManager _wsManager;
    private readonly ILogger<AudioCaptureService>? _logger;
    private Process? _arecordProcess;
    private const string AudioDevice = "hw:1,0"; // USB soundcard card 1, device 0
    private const int SampleRate = 16000; // 16kHz for lower bandwidth
    private const int CaptureChannels = 2; // Record in stereo (most USB soundcards require this)
    private const int OutputChannels = 1; // Convert to mono for transmission
    private const int ChunkSize = 6400; // ~200ms chunks at 16kHz stereo 16-bit (3200 samples * 2 channels * 2 bytes)

    public AudioCaptureService(WebSocketManager wsManager, ILogger<AudioCaptureService>? logger = null)
    {
        _wsManager = wsManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("AudioCaptureService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Start arecord process to capture audio
                // Use stereo capture (most USB soundcards require this)
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/arecord",
                    Arguments = $"-D {AudioDevice} -f S16_LE -r {SampleRate} -c {CaptureChannels} -t raw",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _arecordProcess = Process.Start(psi);
                if (_arecordProcess == null)
                {
                    _logger?.LogWarning("Failed to start arecord, retrying in 5 seconds...");
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                _logger?.LogInformation($"Audio capture started on {AudioDevice}");

                // Read audio chunks and broadcast to clients
                var audioBuffer = new byte[ChunkSize];
                using var stream = _arecordProcess.StandardOutput.BaseStream;
                var chunkCounter = 0;

                while (!stoppingToken.IsCancellationRequested && !_arecordProcess.HasExited)
                {
                    var bytesRead = await stream.ReadAsync(audioBuffer, 0, ChunkSize, stoppingToken);
                    if (bytesRead > 0)
                    {
                        // Convert stereo to mono by averaging left and right channels
                        // Input: stereo 16-bit PCM (interleaved: L R L R ...)
                        // Output: mono 16-bit PCM
                        var stereoSamples = bytesRead / (CaptureChannels * 2); // 2 bytes per sample, 2 channels
                        var monoChunk = new byte[stereoSamples * 2]; // 2 bytes per mono sample

                        // Convert interleaved stereo to mono
                        for (int i = 0; i < stereoSamples; i++)
                        {
                            // Read left and right channel samples (16-bit = 2 bytes each)
                            int leftIdx = i * CaptureChannels * 2;
                            int rightIdx = leftIdx + 2;

                            short left = BitConverter.ToInt16(audioBuffer, leftIdx);
                            short right = BitConverter.ToInt16(audioBuffer, rightIdx);

                            // Average the channels
                            short mono = (short)((left + right) / 2);

                            // Write mono sample
                            int monoIdx = i * 2;
                            BitConverter.GetBytes(mono).CopyTo(monoChunk, monoIdx);
                        }

                        chunkCounter++;
                        if (chunkCounter % 50 == 0) // Log every 50 chunks (~10 seconds)
                        {
                            _logger?.LogInformation($"Broadcasting audio chunk {chunkCounter}: {bytesRead} bytes stereo -> {monoChunk.Length} bytes mono to {_wsManager.GetAllClientIds().Count()} clients");
                        }

                        await _wsManager.BroadcastAudioAsync(monoChunk);
                    }
                    else if (bytesRead == 0)
                    {
                        // End of stream
                        _logger?.LogWarning("arecord stream ended (bytesRead=0)");
                        break;
                    }
                }

                if (_arecordProcess.HasExited)
                {
                    var error = await _arecordProcess.StandardError.ReadToEndAsync();
                    _logger?.LogWarning($"arecord exited with code {_arecordProcess.ExitCode}: {error}");
                    await Task.Delay(2000, stoppingToken); // Wait before retry
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in audio capture, retrying in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
            finally
            {
                try
                {
                    _arecordProcess?.Kill();
                    _arecordProcess?.Dispose();
                    _arecordProcess = null;
                }
                catch { }
            }
        }
    }

    public override void Dispose()
    {
        try
        {
            _arecordProcess?.Kill();
            _arecordProcess?.Dispose();
        }
        catch { }
        base.Dispose();
    }
}

/// <summary>
/// Plays audio chunks received from pilot to rover speaker
/// </summary>
