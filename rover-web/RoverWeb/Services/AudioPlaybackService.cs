sealed class AudioPlaybackService
{
    private readonly ILogger<AudioPlaybackService>? _logger;
    private Process? _aplayProcess;
    private readonly object _playbackLock = new();
    private const string AudioDevice = "hw:1,0"; // USB soundcard card 1, device 0
    private const int SampleRate = 16000; // 16kHz
    private const int PlaybackChannels = 2; // Playback in stereo (USB soundcard requires this)
    private const int InputChannels = 1; // Input is mono
    private readonly Queue<byte[]> _audioQueue = new();
    private bool _isPlaying = false;
    private bool _hornActive = false;
    private CancellationTokenSource? _hornCancellation = null;
    private readonly object _hornLock = new();
    private byte[]? _hornAudioData = null;
    private readonly object _hornDataLock = new();

    public AudioPlaybackService(ILogger<AudioPlaybackService>? logger = null)
    {
        _logger = logger;
        // Load horn audio file on startup
        _ = Task.Run(LoadHornAudioAsync);
    }

    private async Task LoadHornAudioAsync()
    {
        try
        {
            var hornPath = Path.Combine("wwwroot", "horn.mp3");
            if (!File.Exists(hornPath))
            {
                _logger?.LogWarning($"Horn audio file not found at {hornPath}");
                return;
            }

            _logger?.LogInformation($"Loading horn audio from {hornPath}");

            // Use ffmpeg to decode MP3 to raw PCM (16kHz, 16-bit, stereo)
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/ffmpeg",
                Arguments = $"-i \"{hornPath}\" -f s16le -ar {SampleRate} -ac {PlaybackChannels} -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger?.LogError("Failed to start ffmpeg for horn audio decoding");
                return;
            }

            // Read decoded audio into memory
            using var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms);
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger?.LogError($"ffmpeg failed with exit code {process.ExitCode}: {error}");
                return;
            }

            lock (_hornDataLock)
            {
                _hornAudioData = ms.ToArray();
            }

            _logger?.LogInformation($"Horn audio loaded: {_hornAudioData.Length} bytes ({_hornAudioData.Length / (SampleRate * PlaybackChannels * 2)} seconds)");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load horn audio file");
        }
    }

    public void PlayAudioChunk(byte[] audioData)
    {
        lock (_playbackLock)
        {
            _audioQueue.Enqueue(audioData);
            var queueSize = _audioQueue.Count;
            _logger?.LogInformation($"Queued audio chunk: {audioData.Length} bytes, queue size: {queueSize}");

            // Start playback if not already running
            if (!_isPlaying)
            {
                _isPlaying = true;
                _logger?.LogInformation("Starting audio playback loop - queue has {QueueSize} chunks", queueSize);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PlaybackLoop();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Playback loop crashed");
                        lock (_playbackLock)
                        {
                            _isPlaying = false;
                        }
                    }
                });
            }
        }
    }

    private async Task PlaybackLoop()
    {
        var chunkCounter = 0;
        _logger?.LogInformation("PlaybackLoop started");

        while (true)
        {
            byte[]? chunk = null;
            int queueSize = 0;

            lock (_playbackLock)
            {
                queueSize = _audioQueue.Count;
                if (_audioQueue.Count == 0)
                {
                    _isPlaying = false;
                    _logger?.LogInformation("PlaybackLoop: queue empty, stopping");
                }
                else
                {
                    chunk = _audioQueue.Dequeue();
                }
            }

            // If no chunk, wait a bit and check again
            if (chunk == null)
            {
                await Task.Delay(500);

                // Check again after delay
                lock (_playbackLock)
                {
                    if (_audioQueue.Count == 0)
                    {
                        return;
                    }
                    chunk = _audioQueue.Dequeue();
                }

                if (chunk == null) continue;
            }

            try
            {
                // Start aplay process if not running
                if (_aplayProcess == null || _aplayProcess.HasExited)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/aplay",
                        Arguments = $"-D {AudioDevice} -f S16_LE -r {SampleRate} -c {PlaybackChannels} -t raw",
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _aplayProcess = Process.Start(psi);
                    if (_aplayProcess == null)
                    {
                        _logger?.LogWarning("Failed to start aplay");
                        await Task.Delay(100);
                        continue;
                    }

                    _logger?.LogInformation($"aplay started for audio playback (chunk #{chunkCounter}), PID: {_aplayProcess.Id}");

                    // Read stderr in background to catch any errors
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Read stderr line by line
                            while (!_aplayProcess.HasExited)
                            {
                                var line = await _aplayProcess.StandardError.ReadLineAsync();
                                if (line != null)
                                {
                                    _logger?.LogWarning($"aplay stderr: {line}");
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error reading aplay stderr");
                        }
                    });
                }

                // Convert mono to stereo by duplicating the channel
                // Input: mono 16-bit PCM (chunk.Length bytes = chunk.Length/2 samples)
                // Output: stereo 16-bit PCM (interleaved: L R L R ...)
                var monoSamples = chunk.Length / 2; // 2 bytes per sample
                var stereoChunk = new byte[chunk.Length * PlaybackChannels]; // Double the size for stereo

                // Convert using BitConverter (safer, no unsafe code needed)
                for (int i = 0; i < monoSamples; i++)
                {
                    // Read mono sample (16-bit = 2 bytes)
                    int monoIdx = i * 2;
                    short monoSample = BitConverter.ToInt16(chunk, monoIdx);

                    // Write to both left and right channels
                    int stereoIdx = i * PlaybackChannels * 2;
                    BitConverter.GetBytes(monoSample).CopyTo(stereoChunk, stereoIdx);     // Left
                    BitConverter.GetBytes(monoSample).CopyTo(stereoChunk, stereoIdx + 2); // Right
                }

                // Write stereo audio chunk to aplay stdin
                if (_aplayProcess != null && !_aplayProcess.HasExited)
                {
                    try
                    {
                        await _aplayProcess.StandardInput.BaseStream.WriteAsync(stereoChunk, 0, stereoChunk.Length);
                        await _aplayProcess.StandardInput.BaseStream.FlushAsync();
                        if (chunkCounter <= 5 || chunkCounter % 50 == 0)
                        {
                            _logger?.LogInformation($"Wrote {stereoChunk.Length} bytes (mono {chunk.Length} -> stereo) to aplay stdin (chunk #{chunkCounter})");
                        }
                    }
                    catch (Exception writeEx)
                    {
                        _logger?.LogError(writeEx, $"Error writing to aplay stdin: {writeEx.Message}");
                        // Kill and restart aplay
                        try
                        {
                            _aplayProcess?.Kill();
                            _aplayProcess?.Dispose();
                            _aplayProcess = null;
                        }
                        catch { }
                    }
                }
                else
                {
                    _logger?.LogWarning("aplay process is null or has exited, will restart");
                    _aplayProcess = null;
                    continue;
                }

                chunkCounter++;
                if (chunkCounter <= 5 || chunkCounter % 50 == 0)
                {
                    _logger?.LogInformation($"Played {chunkCounter} audio chunks ({chunk.Length} bytes each), queue size: {_audioQueue.Count}, aplay running: {_aplayProcess != null && !_aplayProcess.HasExited}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error playing audio chunk #{chunkCounter}");
                try
                {
                    _aplayProcess?.Kill();
                    _aplayProcess?.Dispose();
                    _aplayProcess = null;
                }
                catch { }
                await Task.Delay(100);
            }
        }
    }

    public void StartHorn()
    {
        lock (_hornLock)
        {
            // If already playing, ignore the press (no queuing)
            if (_hornActive)
            {
                _logger?.LogDebug("Horn already playing, ignoring press");
                return;
            }

            // Check if horn audio is loaded
            lock (_hornDataLock)
            {
                if (_hornAudioData == null || _hornAudioData.Length == 0)
                {
                    _logger?.LogWarning("Horn audio not loaded, cannot play horn");
                    return;
                }
            }

            _hornActive = true;
            _hornCancellation = new CancellationTokenSource();
            _logger?.LogInformation("Horn started - playing horn.mp3 once");

            // Start single horn playback in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await HornGenerationLoop(_hornCancellation.Token);
                }
                finally
                {
                    // Mark as finished when done
                    lock (_hornLock)
                    {
                        _hornActive = false;
                        _hornCancellation?.Dispose();
                        _hornCancellation = null;
                        _logger?.LogInformation("Horn finished");
                    }
                }
            });
        }
    }

    public void StopHorn()
    {
        // No-op: horn plays once and stops automatically
        // This method is kept for API compatibility but does nothing
    }

    private async Task HornGenerationLoop(CancellationToken cancellationToken)
    {
        const int chunkDurationMs = 100; // 100ms chunks
        const int chunkSizeBytes = SampleRate * chunkDurationMs / 1000 * PlaybackChannels * 2; // 16-bit samples

        byte[]? hornData = null;
        lock (_hornDataLock)
        {
            if (_hornAudioData == null || _hornAudioData.Length == 0)
            {
                _logger?.LogError("Horn audio data not available");
                return;
            }
            hornData = _hornAudioData;
        }

        int position = 0; // Current position in the audio data

        // Play through the entire audio file once (no looping)
        while (position < hornData.Length && !cancellationToken.IsCancellationRequested)
        {
            // Extract one chunk from the horn audio
            var hornChunk = new byte[chunkSizeBytes];
            int bytesToCopy = Math.Min(chunkSizeBytes, hornData.Length - position);

            if (bytesToCopy > 0)
            {
                Array.Copy(hornData, position, hornChunk, 0, bytesToCopy);
                position += bytesToCopy;

                // If the chunk is smaller than expected (end of file), pad with zeros
                if (bytesToCopy < chunkSizeBytes)
                {
                    // Fill remainder with silence (zeros)
                    Array.Clear(hornChunk, bytesToCopy, chunkSizeBytes - bytesToCopy);
                }
            }
            else
            {
                // End of audio
                break;
            }

            // Queue the horn audio chunk
            PlayAudioChunk(hornChunk);

            // Wait before playing next chunk
            await Task.Delay(chunkDurationMs, cancellationToken);
        }
    }

    public void Dispose()
    {
        try
        {
            StopHorn();
            _aplayProcess?.Kill();
            _aplayProcess?.Dispose();
        }
        catch { }
    }
}

// ===== Serial Pump =====
