using System.IO.MemoryMappedFiles;
using System.Xml;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

// SCC-1-[0-9]+
// SCPU[0-9]+UTI
// THDD[1-9]+
/*
 * ["SCPUCK", "SCPUUTI", "SUSEDMEM", "SMEMCLK", "SUSEDVMEM", "SGPU1MEMCLK", "SGPU1UTI"]
 * ["TMOBO", "TCHIP", "TCPUDIO", "TGPU1DIO", "TGPU1HOT"]
 * ["FCPU", "FGPU1", "FGPU1GPU2", "FGPU1GPU3"]
 * ["VCPUVDD", "VGPU1"]
 * ["PCPUVDD", "PGPU1"]
*/

class Sensor
{
    public string Id { get; set; }
    public string Label { get; set; }
    public double Value { get; set; }
}

namespace Aida64ReaderService
{
    public class Worker : BackgroundService
    {
        private static readonly string _sysCpuCoreClockPattern = "SCC-1-[0-9]+";
        private static readonly string _sysCpuThreadUtiPattern = "SCPU[0-9]+UTI";
        private static readonly string _tempHddPattern = "THDD[1-9]+";
        private static readonly string _delimitter = "__";

        private readonly Dictionary<string, string> _dictionaryInUsedKeys = new() {
            { "SCPUCK", $"sys_cpu{_delimitter}clock" },
            { "SCPUUTI", $"sys_cpu{_delimitter}utilization" },
            { "SUSEDMEM", $"sys_mem{_delimitter}usage" },
            { "SMEMCLK", $"sys_mem{_delimitter}clock" },
            { "SUSEDVMEM", $"sys_gpu{_delimitter}mem_usage" },
            { "SGPU1MEMCLK", $"sys_gpu{_delimitter}mem_clock" },
            { "SGPU1UTI", $"sys_gpu{_delimitter}utilization" },
            { "TMOBO", $"temp_mobo{_delimitter}measure" },
            { "TCHIP", $"temp_chipset{_delimitter}measure" },
            { "TCPUDIO", $"temp_cpu{_delimitter}measure" },
            { "TGPU1DIO", $"temp_gpu{_delimitter}measure" },
            { "TGPU1HOT", $"temp_gpu{_delimitter}hotspot" },
            { "FCPU", $"fan_cpu{_delimitter}measure" },
            { "FGPU1", $"fan_gpu{_delimitter}fan1" },
            { "FGPU1GPU2", $"fan_gpu{_delimitter}fan2" },
            { "FGPU1GPU3", $"fan_gpu{_delimitter}fan3" },
            { "VCPUVDD", $"voltage_cpu{_delimitter}measure" },
            { "VGPU1", $"voltage_gpu{_delimitter}measure" },
            { "PCPUVDD", $"wattage_cpu{_delimitter}measure" },
            { "PGPU1", $"wattage_gpu{_delimitter}measure" }
        };

        private readonly Dictionary<string, (string, List<double>)> _dictionaryMultiMeasurement = new()
        {
            [_sysCpuCoreClockPattern] = ($"sys_cpu{_delimitter}clock_core_", new List<double>()), // sys_cpu{_delimitter}clock_core_max, sys_cpu{_delimitter}clock_core_min 
            [_sysCpuThreadUtiPattern] = ($"sys_cpu{_delimitter}utilization_thread_", new List<double>()), // sys_cpu{_delimitter}utilization_thread_max, sys_cpu{_delimitter}utilization_thread_min 
        };

        private readonly int _delayedTimeMs = 1000;

        private readonly MQTTClientService _mqttClientService;
        private readonly ILogger<Worker> _logger;

        public Worker(
            MQTTClientService mqttClientService,
            ILogger<Worker> logger
        ) =>
        (_mqttClientService, _logger) = (mqttClientService, logger);

        private static string ReadSysInfoFromAida64()
        {
            using var mmf = MemoryMappedFile.OpenExisting("AIDA64_SensorValues");
            using var accessor = mmf.CreateViewAccessor();
            var bytes = new byte[accessor.Capacity];

            accessor.ReadArray(0, bytes, 0, bytes.Length);

            int i = bytes.Length - 1;
            while (bytes[i] == 0) 
                --i;

            byte[] nonEmptyBytes = new byte[i + 1];
            Array.Copy(bytes, nonEmptyBytes, i + 1);

            return "<root>" + Encoding.ASCII.GetString(nonEmptyBytes).Trim() + "</root>";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // calling services
            await _mqttClientService.ExecuteStartAsync();

            bool connected = _mqttClientService.IsConnected;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!connected)
                {
                    connected = _mqttClientService.IsConnected;
                    Console.WriteLine($"connection is ready? {connected}");

                    if (!connected)
                    {
                        await Task.Delay(5000, stoppingToken);
                    }
                    continue;
                }

                var xmlString = ReadSysInfoFromAida64();

                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlString);

                var nodes = xmlDoc.DocumentElement!.ChildNodes
                    .OfType<XmlNode>()
                    .ToList();
                var filteredNodes = new List<XmlNode>();

                // reset each key's array
                foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                {
                    _dictionaryMultiMeasurement[pattern].Item2.Clear();
                }

                var hddTemps = new List<double>();

                // filter out the nodes that have not-parsable doubles
                // add all multi-measurement nodes' values to their corresponnding lists
                // filter out the not-needed nodes
                foreach (var node in nodes)
                {
                    string id = node.SelectSingleNode("id")!.InnerText;

                    var success = Double.TryParse(node.SelectSingleNode("value")!.InnerText, out double value);

                    if (!success) continue;

                    foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                    {
                        Regex regex = new (pattern);
                        
                        if (regex.IsMatch(id))
                        {
                            var li = _dictionaryMultiMeasurement[pattern].Item2;
                            li.Add(value);
                        }
                    }

                    if (_dictionaryInUsedKeys.ContainsKey(id))
                    {
                        filteredNodes.Add(node);
                    }

                    Regex tempHddRegex = new (_tempHddPattern);

                    if (tempHddRegex.IsMatch(id))
                    {
                        hddTemps.Add(value);
                    }
                }

                var sensors = filteredNodes
                    .Select(n => new Sensor
                    {
                        Id = _dictionaryInUsedKeys[n.SelectSingleNode("id")!.InnerText],
                        Label = "",
                        Value = Double.Parse(n.SelectSingleNode("value")!.InnerText),
                    })
                    .ToList();

                foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                {
                    var prefixKey = _dictionaryMultiMeasurement[pattern].Item1;

                    sensors.Add(new Sensor
                    {
                        Id = prefixKey + "max",
                        Label = "",
                        Value = _dictionaryMultiMeasurement[pattern].Item2.Max()
                    });

                    sensors.Add(new Sensor
                    {
                        Id = prefixKey + "min",
                        Label = "",
                        Value = _dictionaryMultiMeasurement[pattern].Item2.Min()
                    });
                }

                foreach (var item in hddTemps.Select((temp, index) => (temp, index)))
                {
                    sensors.Add(new Sensor
                    {
                        Id = $"temp_hdd{_delimitter}hdd" + (item.index + 1).ToString(),
                        Label = "",
                        Value = item.temp
                    });
                };

                var json = JsonSerializer.Serialize(new
                    {
                        sent = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        delimitter= _delimitter,
                        payload = sensors
                    });
                await _mqttClientService.ExecutePublishAsync(json);
                await Task.Delay(_delayedTimeMs, stoppingToken);

                connected = _mqttClientService.IsConnected;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Background Service...");
            await _mqttClientService.ExecuteStopAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }
    }
}
