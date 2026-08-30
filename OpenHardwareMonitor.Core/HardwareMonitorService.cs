using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.Core;

public sealed class HardwareMonitorService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Computer _computer = new();
    private readonly HardwareUpdateVisitor _updateVisitor = new();
    private readonly Dictionary<string, ISensor> _sensorIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IHardware> _hardwareIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DataPoint>> _history = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _disposed;

    public HardwareSnapshot Snapshot { get; private set; } = HardwareSnapshot.Empty;

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started) return;
            ConfigureComputer(settings.Hardware);
            await Task.Run(() => _computer.Open(settings.IsPortable), cancellationToken);
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HardwareSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_started) throw new InvalidOperationException("Hardware monitoring has not started.");
            return await Task.Run(CaptureSnapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetMinMaxAsync(string? sensorId = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IEnumerable<ISensor> sensors = string.IsNullOrWhiteSpace(sensorId)
                ? _sensorIndex.Values
                : _sensorIndex.TryGetValue(sensorId, out var sensor) ? [sensor] : Array.Empty<ISensor>();
            foreach (var item in sensors)
            {
                item.ResetMin();
                item.ResetMax();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetControlAsync(string sensorId, double? value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sensorIndex.TryGetValue(sensorId, out var sensor) || sensor.Control is null)
                throw new ArgumentException("The requested sensor does not provide a control channel.", nameof(sensorId));

            if (value is null)
            {
                sensor.Control.SetDefault();
                return;
            }

            var controlValue = (float)Math.Clamp(value.Value, sensor.Control.MinSoftwareValue, sensor.Control.MaxSoftwareValue);
            sensor.Control.SetSoftware(controlValue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetParameterAsync(string sensorId, string parameterId, double value, bool useDefault, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sensorIndex.TryGetValue(sensorId, out var sensor))
                throw new ArgumentException("The requested sensor was not found.", nameof(sensorId));
            var parameter = sensor.Parameters.FirstOrDefault(item => string.Equals(item.Identifier.ToString(), parameterId, StringComparison.OrdinalIgnoreCase));
            if (parameter is null)
                throw new ArgumentException("The requested parameter was not found.", nameof(parameterId));
            if (useDefault) parameter.IsDefault = true;
            else parameter.Value = (float)value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<DataPoint> GetHistory(string sensorId)
    {
        lock (_history)
            return _history.TryGetValue(sensorId, out var values) ? values.ToArray() : Array.Empty<DataPoint>();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DataPoint>> GetHistory(IEnumerable<string> sensorIds) =>
        sensorIds.ToDictionary(id => id, id => GetHistory(id), StringComparer.OrdinalIgnoreCase);

    private HardwareSnapshot CaptureSnapshot()
    {
        _updateVisitor.VisitComputer(_computer);
        _sensorIndex.Clear();
        _hardwareIndex.Clear();
        var allSensors = new List<SensorReading>();
        var hardware = _computer.Hardware.Select(item => ProjectHardware(item, allSensors)).ToArray();
        var snapshot = new HardwareSnapshot(DateTimeOffset.Now, hardware, allSensors);
        Snapshot = snapshot;
        return snapshot;
    }

    private HardwareNodeSnapshot ProjectHardware(IHardware hardware, List<SensorReading> allSensors)
    {
        _hardwareIndex[hardware.Identifier.ToString()] = hardware;
        var sensors = hardware.Sensors
            .OrderBy(sensor => sensor.SensorType)
            .ThenBy(sensor => sensor.Index)
            .Select(sensor => ProjectSensor(hardware, sensor))
            .ToArray();
        allSensors.AddRange(sensors);
        var children = hardware.SubHardware.Select(item => ProjectHardware(item, allSensors)).ToArray();
        return new HardwareNodeSnapshot(
            hardware.Identifier.ToString(), hardware.Name, hardware.HardwareType.ToString(),
            new Dictionary<string, string>(hardware.Properties, StringComparer.OrdinalIgnoreCase),
            hardware.GetReport(), sensors, children);
    }

    private SensorReading ProjectSensor(IHardware hardware, ISensor sensor)
    {
        var id = sensor.Identifier.ToString();
        _sensorIndex[id] = sensor;
        var control = sensor.Control;
        double? value = sensor.Value.HasValue ? sensor.Value.Value : null;
        if (value is not null) AddHistory(id, value.Value);
        var normalLimits = sensor as ISensorLimits;
        var criticalLimits = sensor as ICriticalSensorLimits;
        var parameters = sensor.Parameters.Select(parameter => new ParameterReading(
            parameter.Identifier.ToString(), parameter.Name, parameter.Description,
            parameter.Value, parameter.DefaultValue, parameter.IsDefault)).ToArray();
        return new SensorReading(
            id,
            hardware.Identifier.ToString(),
            hardware.Name,
            hardware.HardwareType.ToString(),
            sensor.Name,
            sensor.Name,
            sensor.SensorType.ToString(),
            value,
            sensor.Min,
            sensor.Max,
            GetUnit(sensor.SensorType),
            sensor.IsDefaultHidden,
            control is not null,
            control?.MinSoftwareValue ?? 0,
            control?.MaxSoftwareValue ?? 100,
            control?.SoftwareValue,
            control?.ControlMode == ControlMode.Software,
            normalLimits?.LowLimit,
            normalLimits?.HighLimit,
            criticalLimits?.CriticalLowLimit,
            criticalLimits?.CriticalHighLimit,
            parameters);
    }

    private void AddHistory(string sensorId, double value)
    {
        lock (_history)
        {
            if (!_history.TryGetValue(sensorId, out var points))
                _history[sensorId] = points = new Queue<DataPoint>();
            points.Enqueue(new DataPoint(DateTimeOffset.Now, value));
            while (points.Count > 360) points.Dequeue();
        }
    }

    private void ConfigureComputer(HardwareSettings settings)
    {
        _computer.IsMotherboardEnabled = settings.Motherboard;
        _computer.IsCpuEnabled = settings.Cpu;
        _computer.IsMemoryEnabled = settings.Memory;
        _computer.IsGpuEnabled = settings.Gpu;
        _computer.IsStorageEnabled = settings.Storage;
        _computer.IsNetworkEnabled = settings.Network;
        _computer.IsBatteryEnabled = settings.Battery;
        _computer.IsControllerEnabled = settings.Controller;
        _computer.IsPsuEnabled = settings.Psu;
    }

    public static string GetUnit(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "C",
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Timing => "ns",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "uS/cm",
        _ => string.Empty
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            if (_started) await Task.Run(_computer.Close);
            _started = false;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardwareMonitorService));
    }

    private sealed class HardwareUpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            foreach (var hardware in computer.Hardware) VisitHardware(hardware);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var child in hardware.SubHardware) VisitHardware(child);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
