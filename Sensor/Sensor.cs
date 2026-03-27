using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Sensor
{
    class Sensor
    {
        private static string _sensorId = "S101";
        private static string _zona     = "ZONA_CENTRO";
        private static string _gwHost   = "127.0.0.1";
        private static int    _gwPort   = Shared.Protocol.GATEWAY_PORT;

        private static TcpClient?    _client;
        private static StreamWriter? _writer;
        private static StreamReader? _reader;
        private static readonly object _ioLock = new();

        // Tipos de dados que este sensor suporta
        private static List<string> _tiposSuportados = new() { "TEMP", "HUM", "RUIDO" };

        // Controlo do heartbeat
        private static Timer? _heartbeatTimer;
        private static bool   _connected = false;

        static void Main(string[] args)
        {
            // Argumentos: sensor_id zona gateway_host gateway_port
            if (args.Length > 0) _sensorId = args[0];
            if (args.Length > 1) _zona     = args[1];
            if (args.Length > 2) _gwHost   = args[2];
            if (args.Length > 3 && int.TryParse(args[3], out int parsedGwPort)) _gwPort = parsedGwPort;

            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║        SENSOR – Sistema de Monitorização Urbana      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");
            Console.WriteLine($"  ID: {_sensorId}  |  Zona: {_zona}");
            Console.WriteLine($"  Gateway: {_gwHost}:{_gwPort}");
            Console.WriteLine();

            if (!Ligar()) return;

            MenuPrincipal();

            Desligar();
        }

        // ─── Ligação ─────────────────────────────────────────────────────────

        static bool Ligar()
        {
            try
            {
                Console.WriteLine("[SENSOR] A ligar ao gateway...");
                _client = new TcpClient(_gwHost, _gwPort);
                var ns  = _client.GetStream();
                _writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _reader = new StreamReader(ns, Encoding.UTF8);

                // HELLO
                var resp = EnviarEResponder(Shared.Protocol.Build(Shared.Protocol.HELLO, _sensorId, _zona));
                Console.WriteLine($"[SENSOR] Gateway: {resp}");

                if (resp == null || !resp.StartsWith(Shared.Protocol.WELCOME))
                {
                    Console.WriteLine("[SENSOR] Gateway rejeitou a ligação.");
                    return false;
                }

                // REGISTER – indicar os tipos de dados suportados
                resp = EnviarEResponder(Shared.Protocol.Build(
                    Shared.Protocol.REGISTER, _sensorId, string.Join(",", _tiposSuportados)));
                Console.WriteLine($"[SENSOR] Gateway: {resp}");

                _connected = true;

                // Iniciar heartbeat a cada 10 segundos
                _heartbeatTimer = new Timer(_ => EnviarHeartbeat(), null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

                Console.WriteLine("[SENSOR] Ligado com sucesso!\n");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SENSOR] Erro ao ligar: {ex.Message}");
                return false;
            }
        }

        static void Desligar()
        {
            if (!_connected) return;
            _heartbeatTimer?.Dispose();
            var resp = EnviarEResponder(Shared.Protocol.Build(Shared.Protocol.BYE, _sensorId));
            Console.WriteLine($"[SENSOR] Gateway: {resp}");
            _client?.Close();
            _connected = false;
            Console.WriteLine("[SENSOR] Desligado.");
        }

        // ─── Heartbeat ───────────────────────────────────────────────────────

        static void EnviarHeartbeat()
        {
            if (!_connected) return;
            try
            {
                string ts = DateTime.Now.ToString("s");
                _ = EnviarEResponder(Shared.Protocol.Build(Shared.Protocol.HEARTBEAT, _sensorId, ts), showOutgoing: false);
                // Heartbeat silencioso (sem mostrar no menu ativo)
            }
            catch { /* ignora erros de heartbeat */ }
        }

        // ─── Menu de Texto ───────────────────────────────────────────────────

        static void MenuPrincipal()
        {
            while (true)
            {
                Console.WriteLine("\n┌─────────────────────────────────────┐");
                Console.WriteLine("│             MENU DO SENSOR           │");
                Console.WriteLine("├─────────────────────────────────────┤");
                Console.WriteLine("│  1. Enviar medição ambiental         │");
                Console.WriteLine("│  2. Enviar stream de vídeo           │");
                Console.WriteLine("│  3. Enviar heartbeat manual          │");
                Console.WriteLine("│  4. Ver tipos de dados suportados    │");
                Console.WriteLine("│  5. Sair                             │");
                Console.WriteLine("└─────────────────────────────────────┘");
                Console.Write("Opção: ");

                var op = Console.ReadLine()?.Trim();
                switch (op)
                {
                    case "1": MenuEnviarMedicao(); break;
                    case "2": MenuEnviarVideo();   break;
                    case "3": EnviarHeartbeatManual(); break;
                    case "4": MostrarTipos();      break;
                    case "5": return;
                    default:  Console.WriteLine("Opção inválida."); break;
                }
            }
        }

        static void MenuEnviarMedicao()
        {
            Console.WriteLine("\n── Enviar Medição ──────────────────────");
            Console.WriteLine("Tipos disponíveis: " + string.Join(", ", _tiposSuportados));
            Console.Write("Tipo de dado (ex: TEMP): ");
            var tipo = Console.ReadLine()?.Trim().ToUpper() ?? "";

            if (!_tiposSuportados.Contains(tipo))
            {
                Console.WriteLine($"⚠ Tipo '{tipo}' não suportado por este sensor.");
                Console.Write("Enviar mesmo assim? (s/N): ");
                if (Console.ReadLine()?.Trim().ToLower() != "s") return;
            }

            Console.Write($"Valor para {tipo}: ");
            var valor = Console.ReadLine()?.Trim() ?? "0";

            string timestamp = DateTime.Now.ToString("s");
            var msg = Shared.Protocol.Build(
                Shared.Protocol.DATA, _sensorId, _zona, tipo, valor, timestamp);

            var resp = EnviarEResponder(msg);
            Console.WriteLine($"[SENSOR] Gateway: {resp}");

            if (resp != null && resp.StartsWith(Shared.Protocol.ACK))
                Console.WriteLine($"✔ Medição [{tipo}={valor}] enviada com sucesso.");
            else
                Console.WriteLine($"✘ Falha no envio da medição.");
        }

        static void MenuEnviarVideo()
        {
            Console.WriteLine("\n── Stream de Vídeo ─────────────────────");
            Console.Write("Porta ou URL do stream: ");
            var info = Console.ReadLine()?.Trim() ?? "8080";

            var msg = Shared.Protocol.Build(
                Shared.Protocol.VIDEO_STREAM, _sensorId, _zona, info);
            var resp = EnviarEResponder(msg);
            Console.WriteLine($"[SENSOR] Gateway: {resp}");
        }

        static void EnviarHeartbeatManual()
        {
            Console.WriteLine("\n── Heartbeat Manual ────────────────────");
            string ts = DateTime.Now.ToString("s");
            var resp = EnviarEResponder(Shared.Protocol.Build(Shared.Protocol.HEARTBEAT, _sensorId, ts));
            Console.WriteLine($"[SENSOR] Gateway: {resp}");
            Console.WriteLine("✔ Heartbeat enviado.");
        }

        static void MostrarTipos()
        {
            Console.WriteLine("\n── Tipos de Dados Suportados ───────────");
            foreach (var t in _tiposSuportados)
                Console.WriteLine($"  • {t}");
        }

        static void EnviarMensagem(string msg)
        {
            Console.WriteLine($"[SENSOR] → {msg}");
            _writer?.WriteLine(msg);
        }

        static string? EnviarEResponder(string msg, bool showOutgoing = true)
        {
            lock (_ioLock)
            {
                if (showOutgoing) EnviarMensagem(msg);
                else _writer?.WriteLine(msg);
                return _reader?.ReadLine();
            }
        }
    }
}
