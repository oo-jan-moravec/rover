sealed class GpioController : IDisposable
{
    private const int IrLedPin = 4;
    private const int HeadlightPin = 27;
    private System.Device.Gpio.GpioController? _controller;
    private bool _gpioAvailable = false;
    private bool _headlightOn = false;
    private bool _irLedOn = false;
    private readonly ILogger<GpioController>? _logger;

    public GpioController(ILogger<GpioController>? logger = null)
    {
        _logger = logger;

        try
        {
            _controller = new System.Device.Gpio.GpioController();

            _controller.OpenPin(IrLedPin, PinMode.Output);
            _controller.Write(IrLedPin, PinValue.Low);

            _controller.OpenPin(HeadlightPin, PinMode.Output);
            _controller.Write(HeadlightPin, PinValue.Low);

            _gpioAvailable = true;
            _logger?.LogInformation($"GPIO initialized successfully. IR (Pin {IrLedPin}) and Headlight (Pin {HeadlightPin}) set to output.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GPIO not available, light control disabled");
            _gpioAvailable = false;
            _controller?.Dispose();
            _controller = null;
        }
    }

    public void SetHeadlight(bool on)
    {
        _headlightOn = on;

        if (!_gpioAvailable || _controller == null)
        {
            _logger?.LogWarning("GPIO not available, cannot set headlight");
            return;
        }

        try
        {
            var pinValue = on ? PinValue.High : PinValue.Low;
            _controller.Write(HeadlightPin, pinValue);
            _logger?.LogInformation($"Headlight turned {(on ? "ON" : "OFF")} (GPIO {HeadlightPin} = {(on ? "HIGH" : "LOW")})");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set Headlight GPIO");
        }
    }

    public void SetIrLed(bool on)
    {
        _irLedOn = on;

        if (!_gpioAvailable || _controller == null)
        {
            _logger?.LogWarning("GPIO not available, cannot set IR LED");
            return;
        }

        try
        {
            var pinValue = on ? PinValue.High : PinValue.Low;
            _controller.Write(IrLedPin, pinValue);
            _logger?.LogInformation($"IR LED turned {(on ? "ON" : "OFF")} (GPIO {IrLedPin} = {(on ? "HIGH" : "LOW")})");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set IR LED GPIO");
        }
    }

    public bool GetHeadlightState() => _headlightOn;
    public bool GetIrLedState() => _irLedOn;

    public void Dispose()
    {
        if (_gpioAvailable && _controller != null)
        {
            try
            {
                // Turn off lights
                _controller.Write(IrLedPin, PinValue.Low);
                _controller.Write(HeadlightPin, PinValue.Low);
                _controller.ClosePin(IrLedPin);
                _controller.ClosePin(HeadlightPin);
            }
            catch { }

            _controller?.Dispose();
        }
    }
}

// ===== Wi-Fi Reliability System =====

/// <summary>
/// Multi-metric WiFi health scoring system for proactive monitoring
/// </summary>
