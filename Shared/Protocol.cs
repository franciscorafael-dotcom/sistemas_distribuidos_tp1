namespace Shared
{
    /// <summary>
    /// Protocolo de comunicação SENSOR/GATEWAY/SERVIDOR
    /// Formato das mensagens: TIPO|campo1|campo2|...
    /// </summary>
    public static class Protocol
    {
        // ─── Mensagens SENSOR → GATEWAY ───────────────────────────────────────
        public const string HELLO       = "HELLO";       // HELLO|sensor_id|zona
        public const string REGISTER    = "REGISTER";    // REGISTER|sensor_id|tipo1,tipo2,...
        public const string DATA        = "DATA";        // DATA|sensor_id|zona|tipo|valor|timestamp
        public const string HEARTBEAT   = "HEARTBEAT";   // HEARTBEAT|sensor_id|timestamp
        public const string BYE         = "BYE";         // BYE|sensor_id

        // ─── Mensagens GATEWAY → SENSOR ───────────────────────────────────────
        public const string WELCOME     = "WELCOME";     // WELCOME|sensor_id        (aceite)
        public const string REJECT      = "REJECT";      // REJECT|sensor_id|motivo  (recusado)
        public const string ACK         = "ACK";         // ACK|ref_tipo             (confirmação)
        public const string NACK        = "NACK";        // NACK|ref_tipo|motivo     (erro)

        // ─── Mensagens GATEWAY → SERVIDOR ─────────────────────────────────────
        public const string GW_DATA     = "GW_DATA";    // GW_DATA|gw_id|sensor_id|zona|tipo|valor|timestamp
        public const string GW_HELLO    = "GW_HELLO";   // GW_HELLO|gw_id
        public const string GW_BYE      = "GW_BYE";     // GW_BYE|gw_id

        // ─── Mensagens SERVIDOR → GATEWAY ─────────────────────────────────────
        public const string SRV_ACK     = "SRV_ACK";    // SRV_ACK
        public const string SRV_NACK    = "SRV_NACK";   // SRV_NACK|motivo

        public const char   SEP         = '|';
        public const string ENCODING    = "UTF-8";
        public const int    BUFFER_SIZE = 4096;

        // Portas por omissão
        public const int GATEWAY_PORT  = 9000;
        public const int SERVIDOR_PORT = 9100;

        // Heartbeat: se o gateway não recebe heartbeat neste intervalo → sensor inativo
        public const int HEARTBEAT_TIMEOUT_SECONDS = 30;

        public static string Build(params string[] parts) => string.Join(SEP, parts);

        public static string[] Parse(string message) => message.Split(SEP);
    }
}
