using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Gateway
{
    // Registo de um sensor no ficheiro CSV
    class SensorRecord
    {
        public string SensorId   { get; set; } = "";
        public string Estado     { get; set; } = "ativo";
        public string Zona       { get; set; } = "";
        public List<string> Tipos { get; set; } = new();
        public DateTime LastSync  { get; set; } = DateTime.Now;
    }

    class Gateway
    {
        private const string GW_ID      = "GW01";
        private const string CSV_PATH   = "sensores.csv";
        private const string LOG_DIR    = "logs_gateway";

        // Sensores registados (carregados do CSV)
        private static Dictionary<string, SensorRecord> _sensores = new();
        private static readonly object _sensorLock = new();
        private static readonly Mutex _csvMutex = new();

        // Heartbeat tracking: sensor_id → última vez que recebeu heartbeat
        private static Dictionary<string, DateTime> _heartbeats = new();

        // Ligação ao Servidor
        private static TcpClient? _srvClient;
        private static StreamWriter? _srvWriter;
        private static StreamReader? _srvReader;
        private static readonly object _srvLock = new();

        // Endereço do servidor
        private static string _srvHost = "127.0.0.1";
        private static int    _srvPort = Shared.Protocol.SERVIDOR_PORT;

        static void Main(string[] args)
        {
            // Argumentos opcionais: gateway_port servidor_host servidor_port
            int gwPort = Shared.Protocol.GATEWAY_PORT;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedGwPort)) gwPort = parsedGwPort;
            if (args.Length > 1) _srvHost = args[1];
            if (args.Length > 2 && int.TryParse(args[2], out int parsedSrvPort)) _srvPort = parsedSrvPort;

            Directory.CreateDirectory(LOG_DIR);
            CarregarCSV();

            // Ligar ao servidor
            LigarServidor();

            // Thread que verifica heartbeat dos sensores
            var hbThread = new Thread(MonitorHeartbeats) { IsBackground = true };
            hbThread.Start();

            // Escutar sensores
            var listener = new TcpListener(IPAddress.Any, gwPort);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.WriteLine($"[GATEWAY] Erro: porta {gwPort} já está em uso. Fecha a instância anterior ou usa outra porta.");
                return;
            }
            Console.WriteLine($"[GATEWAY {GW_ID}] Aguardando sensores na porta {gwPort}...");

            while (true)
            {
                var client = listener.AcceptTcpClient();
                var t = new Thread(() => HandleSensor(client));
                t.IsBackground = true;
                t.Start();
            }
        }

        // ─── Ligação ao Servidor ─────────────────────────────────────────────

        static void LigarServidor()
        {
            try
            {
                _srvClient = new TcpClient(_srvHost, _srvPort);
                var ns = _srvClient.GetStream();
                _srvWriter = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _srvReader = new StreamReader(ns, Encoding.UTF8);

                // Handshake com servidor
                _srvWriter.WriteLine(Shared.Protocol.Build(Shared.Protocol.GW_HELLO, GW_ID));
                var resp = _srvReader.ReadLine();
                Console.WriteLine($"[GATEWAY] Servidor respondeu: {resp}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] AVISO: Não foi possível ligar ao servidor: {ex.Message}");
                _srvClient = null;
            }
        }

        static void EnviarParaServidor(string mensagem)
        {
            lock (_srvLock)
            {
                try
                {
                    if (_srvWriter == null)
                    {
                        Console.WriteLine("[GATEWAY] Servidor não disponível. Dados perdidos.");
                        return;
                    }
                    _srvWriter.WriteLine(mensagem);
                    var ack = _srvReader?.ReadLine();
                    Console.WriteLine($"[GATEWAY] Servidor ACK: {ack}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GATEWAY] Erro ao enviar para servidor: {ex.Message}");
                }
            }
        }

        // ─── Gestão do CSV ───────────────────────────────────────────────────

        static void CarregarCSV()
        {
            if (!File.Exists(CSV_PATH))
            {
                Console.WriteLine($"[GATEWAY] Ficheiro '{CSV_PATH}' não encontrado. Sem sensores pré-registados.");
                return;
            }

            _csvMutex.WaitOne();
            try
            {
                var lines = File.ReadAllLines(CSV_PATH);
                foreach (var line in lines)
                {
                    if (line.StartsWith("sensor_id") || string.IsNullOrWhiteSpace(line))
                        continue;

                    // sensor_id:estado:zona:[tipos]:last_sync
                    var parts = line.Split(':', 5);
                    if (parts.Length < 5) continue;

                    var rec = new SensorRecord
                    {
                        SensorId = parts[0],
                        Estado   = parts[1],
                        Zona     = parts[2],
                        LastSync = DateTime.TryParse(parts[4], out var dt) ? dt : DateTime.Now
                    };

                    // Tipos: [TEMP,HUM]
                    var tiposRaw = parts[3].Trim('[', ']');
                    rec.Tipos = new List<string>(
                        tiposRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                    _sensores[rec.SensorId] = rec;
                }
                Console.WriteLine($"[GATEWAY] {_sensores.Count} sensores carregados do CSV.");
            }
            finally
            {
                _csvMutex.ReleaseMutex();
            }
        }

        static void AtualizarCSV()
        {
            _csvMutex.WaitOne();
            try
            {
                using var sw = new StreamWriter(CSV_PATH, append: false, Encoding.UTF8);
                sw.WriteLine("sensor_id:estado:zona:[tipos_dados]:last_sync");
                lock (_sensorLock)
                {
                    foreach (var rec in _sensores.Values)
                    {
                        string tipos = $"[{string.Join(",", rec.Tipos)}]";
                        sw.WriteLine($"{rec.SensorId}:{rec.Estado}:{rec.Zona}:{tipos}:{rec.LastSync:s}");
                    }
                }
            }
            finally
            {
                _csvMutex.ReleaseMutex();
            }
        }

        // ─── Heartbeat Monitor ───────────────────────────────────────────────

        static void MonitorHeartbeats()
        {
            while (true)
            {
                Thread.Sleep(10_000); // verificar de 10 em 10 segundos
                lock (_sensorLock)
                {
                    var agora = DateTime.Now;
                    foreach (var kv in _heartbeats)
                    {
                        var diff = (agora - kv.Value).TotalSeconds;
                        if (diff > Shared.Protocol.HEARTBEAT_TIMEOUT_SECONDS)
                        {
                            if (_sensores.TryGetValue(kv.Key, out var rec) && rec.Estado == "ativo")
                            {
                                rec.Estado = "manutencao";
                                Console.WriteLine($"[GATEWAY] ⚠ Sensor '{kv.Key}' sem heartbeat há {diff:F0}s → marcado como 'manutencao'.");
                                AtualizarCSV();
                            }
                        }
                    }
                }
            }
        }

        // ─── Tratamento de um Sensor ─────────────────────────────────────────

        static void HandleSensor(TcpClient client)
        {
            string sensorId = "DESCONHECIDO";
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    Console.WriteLine($"[GATEWAY] ← [{sensorId}@{ep}] {line}");
                    var parts = Shared.Protocol.Parse(line);
                    var tipo  = parts[0];

                    switch (tipo)
                    {
                        // ── HELLO ──────────────────────────────────────────────
                        case Shared.Protocol.HELLO:
                        {
                            // HELLO|sensor_id|zona
                            if (parts.Length < 3) { writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.REJECT, "?", "Formato inválido")); break; }

                            sensorId  = parts[1];
                            string zona = parts[2];

                            lock (_sensorLock)
                            {
                                if (!_sensores.TryGetValue(sensorId, out var rec))
                                {
                                    // Sensor desconhecido: registar automaticamente
                                    rec = new SensorRecord { SensorId = sensorId, Zona = zona, Estado = "ativo" };
                                    _sensores[sensorId] = rec;
                                    Console.WriteLine($"[GATEWAY] Sensor '{sensorId}' auto-registado.");
                                }

                                if (rec.Estado == "desativado")
                                {
                                    writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.REJECT, sensorId, "Sensor desativado"));
                                    return;
                                }

                                rec.LastSync = DateTime.Now;
                                _heartbeats[sensorId] = DateTime.Now;
                            }

                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.WELCOME, sensorId));
                            EnviarParaServidor(Shared.Protocol.Build(
                                Shared.Protocol.GW_SENSOR_START, GW_ID, sensorId, zona, DateTime.Now.ToString("s")));
                            AtualizarCSV();
                            break;
                        }

                        // ── REGISTER ───────────────────────────────────────────
                        case Shared.Protocol.REGISTER:
                        {
                            // REGISTER|sensor_id|TEMP,HUM,...
                            if (parts.Length < 3) { writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, "REGISTER", "Formato inválido")); break; }

                            sensorId = parts[1];
                            var tipos = new List<string>(parts[2].Split(','));

                            lock (_sensorLock)
                            {
                                if (_sensores.TryGetValue(sensorId, out var rec))
                                    rec.Tipos = tipos;
                            }

                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.ACK, "REGISTER"));
                            EnviarParaServidor(Shared.Protocol.Build(
                                Shared.Protocol.GW_SENSOR_REGISTER, GW_ID, sensorId, parts[2], DateTime.Now.ToString("s")));
                            AtualizarCSV();
                            break;
                        }

                        // ── DATA ───────────────────────────────────────────────
                        case Shared.Protocol.DATA:
                        {
                            // DATA|sensor_id|zona|tipo_dado|valor|timestamp
                            if (parts.Length < 6) { writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, "DATA", "Formato inválido")); break; }

                            string sid       = parts[1];
                            string zona      = parts[2];
                            string tipoDado  = parts[3];
                            string valor     = parts[4];
                            string timestamp = parts[5];

                            bool valido = false;
                            lock (_sensorLock)
                            {
                                if (_sensores.TryGetValue(sid, out var rec))
                                {
                                    if (rec.Estado != "ativo")
                                    {
                                        writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, "DATA", $"Sensor em estado '{rec.Estado}'"));
                                        break;
                                    }
                                    if (!rec.Tipos.Contains(tipoDado))
                                    {
                                        writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, "DATA", $"Tipo '{tipoDado}' não suportado por este sensor"));
                                        break;
                                    }
                                    rec.LastSync = DateTime.Now;
                                    valido = true;
                                }
                            }

                            if (!valido)
                            {
                                writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, "DATA", "Sensor não registado"));
                                break;
                            }

                            // Log local
                            GravarLogLocal(sid, zona, tipoDado, valor, timestamp);

                            // Encaminhar para servidor
                            var gwMsg = Shared.Protocol.Build(
                                Shared.Protocol.GW_DATA, GW_ID, sid, zona, tipoDado, valor, timestamp);
                            EnviarParaServidor(gwMsg);

                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.ACK, "DATA"));
                            AtualizarCSV();
                            break;
                        }

                        // ── HEARTBEAT ──────────────────────────────────────────
                        case Shared.Protocol.HEARTBEAT:
                        {
                            // HEARTBEAT|sensor_id|timestamp
                            if (parts.Length >= 2)
                            {
                                string sid = parts[1];
                                lock (_sensorLock)
                                {
                                    _heartbeats[sid] = DateTime.Now;
                                    if (_sensores.TryGetValue(sid, out var rec))
                                    {
                                        rec.LastSync = DateTime.Now;
                                        if (rec.Estado == "manutencao")
                                        {
                                            rec.Estado = "ativo";
                                            Console.WriteLine($"[GATEWAY] Sensor '{sid}' voltou a 'ativo'.");
                                        }
                                    }
                                }
                                writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.ACK, "HEARTBEAT"));
                                AtualizarCSV();
                            }
                            break;
                        }

                        // ── VIDEO ──────────────────────────────────────────────
                        case Shared.Protocol.VIDEO_STREAM:
                        {
                            // VIDEO|sensor_id|zona|info
                            Console.WriteLine($"[GATEWAY] Stream de vídeo solicitado por '{sensorId}'.");
                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.ACK, "VIDEO"));
                            break;
                        }

                        // ── BYE ────────────────────────────────────────────────
                        case Shared.Protocol.BYE:
                        {
                            // BYE|sensor_id
                            Console.WriteLine($"[GATEWAY] Sensor '{sensorId}' desconectou.");
                            EnviarParaServidor(Shared.Protocol.Build(
                                Shared.Protocol.GW_SENSOR_END, GW_ID, sensorId, DateTime.Now.ToString("s")));
                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.ACK, "BYE"));
                            return;
                        }

                        default:
                            writer.WriteLine(Shared.Protocol.Build(Shared.Protocol.NACK, tipo, "Mensagem desconhecida"));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] Erro com sensor '{sensorId}': {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        static void GravarLogLocal(string sensorId, string zona, string tipoDado, string valor, string timestamp)
        {
            var path = Path.Combine(LOG_DIR, $"{tipoDado}.csv");
            var mutex = new Mutex(false, $"gw_log_{tipoDado}");
            mutex.WaitOne();
            try
            {
                bool exists = File.Exists(path);
                using var sw = new StreamWriter(path, append: true, Encoding.UTF8);
                if (!exists) sw.WriteLine("timestamp,sensor_id,zona,tipo,valor");
                sw.WriteLine($"{timestamp},{sensorId},{zona},{tipoDado},{valor}");
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
