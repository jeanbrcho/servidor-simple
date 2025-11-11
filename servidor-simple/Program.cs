using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

class SimpleSocketHttpServer
{
    static async Task Main(string[] args)
    {
        // Cargar configuración
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // lee las claves Server:BaseDirectory y Server:Port
        string rootDirConfig = config["Server:BaseDirectory"] ?? "wwwroot";
        if (!Path.IsPathRooted(rootDirConfig))
            rootDirConfig = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rootDirConfig));
        int port = int.TryParse(config["Server:Port"], out var p) ? p : 8080;


        // Asegurar carpetas
        Directory.CreateDirectory(rootDirConfig);
        EnsureLogsFolderExists();

        Console.WriteLine($"Carpeta raíz (files): {rootDirConfig}");
        Console.WriteLine($"Puerto: {port}");

        //crea un socket TCP
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(200);

        Console.WriteLine("Servidor iniciado. Esperando conexiones...");

        /* Este bucle nunca termina:
        Cada vez que alguien (por ejemplo, un navegador) se conecta,
        Crea una tarea paralela(Task.Run) para manejar esa conexión.
        Esto permite manejar múltiples usuarios al mismo tiempo (concurrencia asíncrona)*/
        while (true)
        {
            Socket client = await listener.AcceptAsync();
            _ = Task.Run(() => HandleConnectionAsync(client, rootDirConfig)); // concurrencia
        }
    }

    static async Task HandleConnectionAsync(Socket clientSocket, string rootDirectory)
    {
        string remoteIp = (clientSocket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        try
        {
            using var networkStream = new NetworkStream(clientSocket, ownsSocket: true);

            // Reader: leer líneas de texto (headers)
            using var reader = new StreamReader(networkStream, Encoding.UTF8, leaveOpen: true);

            //Leer request-line
            string requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;
            Console.WriteLine($"\n Solicitud: {requestLine} (desde {remoteIp})");

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                await SendSimpleResponse(networkStream, "400 Bad Request", "text/plain", Encoding.UTF8.GetBytes("Bad Request"));
                return;
            }

            string method = parts[0].ToUpperInvariant();
            string rawUrl = parts[1];

            //Leer headers en diccionario
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    var name = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    headers[name] = val;
                }
            }

            //Detectar Content-Length y Accept-Encoding
            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var clv)) contentLength = clv;
            bool acceptsGzip = headers.TryGetValue("Accept-Encoding", out var ae) && ae?.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;

            //Separar path y query string
            string path = rawUrl;
            string queryString = "";
            int qpos = rawUrl.IndexOf('?');
            if (qpos >= 0)
            {
                path = rawUrl.Substring(0, qpos);
                queryString = rawUrl.Substring(qpos + 1);
            }
            // envia index si no se especifica archivo
            if (path == "/") path = "/index.html";

            // Evitar path traversal y mapear a archivo
            string requestedRelative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(rootDirectory, requestedRelative));
            string rootFull = Path.GetFullPath(rootDirectory);
            if (!fullPath.StartsWith(rootFull))
            {
                await Send404(networkStream, path, rootDirectory);
                return;
            }


            // Leer body si POST
            string body = "";
            if (method == "POST" && contentLength > 0)
            {
                var buffer = new char[contentLength];
                int readTotal = 0;
                while (readTotal < contentLength)
                {
                    int r = await reader.ReadAsync(buffer, readTotal, contentLength - readTotal);
                    if (r == 0) break;
                    readTotal += r;
                }
                body = new string(buffer, 0, readTotal);
            }

            // 9 Loguear solicitud: siempre (IP, método, path)
            LogRequest(remoteIp, method, path, string.IsNullOrWhiteSpace(body) ? null : body);

            // Loguear query params (si hay)
            if (!string.IsNullOrEmpty(queryString))
            {
                var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in pairs)
                {
                    var kv = p.Split('=', 2);
                    var k = WebUtility.UrlDecode(kv[0]);
                    var v = kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : "";
                    LogRequest(remoteIp, method, path, $"QUERY {k}={v}");
                }
            }

            // Responder segun método
            if (method == "GET")
            {
                await HandleGetAsync(networkStream, fullPath, acceptsGzip);
            }
            else if (method == "POST")
            {
                // Para POST no hacemos otra cosa que loguear (ya se logueó arriba) y devolver 200
                string respHtml = "<html><body><h1>200 OK</h1><p>POST recibido</p></body></html>";
                var respBytes = Encoding.UTF8.GetBytes(respHtml);
                await SendResponse(networkStream, "200 OK", "text/html; charset=utf-8", respBytes);
            }
            else
            {
                await SendSimpleResponse(networkStream, "405 Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method Not Allowed"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error con {remoteIp}: {ex.Message}");
        }
        finally
        {
            try { clientSocket.Shutdown(SocketShutdown.Both); } catch { }
            clientSocket.Close();
        }
    }

    // Manejar GET y compresión gzip si corresponde
    static async Task HandleGetAsync(NetworkStream stream, string fullPath, bool gzip)
    {
        if (!File.Exists(fullPath))
        {
            await Send404(stream, fullPath, Path.GetDirectoryName(fullPath) ?? "");
            return;
        }

        byte[] content = await File.ReadAllBytesAsync(fullPath);
        string contentType = GetContentType(fullPath);

        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gz.WriteAsync(content, 0, content.Length);
            }
            byte[] compressed = ms.ToArray();
            await SendResponseRaw(stream, "200 OK", contentType, compressed, additionalHeaders: new Dictionary<string, string> { { "Content-Encoding", "gzip" } });
        }
        else
        {
            await SendResponseRaw(stream, "200 OK", contentType, content);
        }
    }

    // Envia respuesta con headers + body (usa stream para escribir binario)
    static async Task SendResponseRaw(NetworkStream stream, string status, string contentType, byte[] body, Dictionary<string, string>? additionalHeaders = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        if (additionalHeaders != null)
        {
            foreach (var kv in additionalHeaders) sb.Append($"{kv.Key}: {kv.Value}\r\n");
        }
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await stream.WriteAsync(body, 0, body.Length);
        await stream.FlushAsync();
    }

    static Task SendResponse(NetworkStream stream, string status, string contentType, byte[] body)
        => SendResponseRaw(stream, status, contentType, body, null);

    static Task SendSimpleResponse(NetworkStream stream, string status, string contentType, byte[] body)
        => SendResponse(stream, status, contentType, body);

    static async Task Send404(NetworkStream stream, string requestedPath, string rootDirectory = "")
    {
        try
        {
            // Buscar un archivo 404.html personalizado dentro del root real
            string custom404 = Path.Combine(rootDirectory, "404.html");
            if (File.Exists(custom404))
            {
                byte[] content = await File.ReadAllBytesAsync(custom404);
                await SendResponseRaw(stream, "404 Not Found", GetContentType(custom404), content);
                return;
            }

            // Si no hay archivo personalizado, devolver HTML básico
            string html = $@"
        <html>
            <head>
                <title>404 - No encontrado</title>
            </head>
            <body>
                <h1>404 Not Found</h1>
                <p>El recurso solicitado no fue encontrado en el servidor.</p>
            </body>
        </html>";
            byte[] b = Encoding.UTF8.GetBytes(html);
            await SendResponse(stream, "404 Not Found", "text/html; charset=utf-8", b);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enviando 404: {ex.Message}");
        }
    }



    // MIME types básicos
    static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }

    // Logging: escribe en carpeta logs en la raíz del proyecto (no en bin)
    static void LogRequest(string ip, string method, string url, string? body = null)
    {
        try
        {
            string execFolder = AppContext.BaseDirectory; // bin/Debug/netX.X/
            string projectRoot = Path.GetFullPath(Path.Combine(execFolder, "..", "..", "..")); // subir hasta carpeta del proyecto
            //crea si no existe la carpeta
            string logsFolder = Path.Combine(projectRoot, "logs");
            Directory.CreateDirectory(logsFolder);

            string file = Path.Combine(logsFolder, $"{DateTime.Now:yyyy-MM-dd}.log");
            string time = DateTime.Now.ToString("HH:mm:ss");

            string entry = $"[{time}] {ip} {method} {url}";
            if (!string.IsNullOrEmpty(body)) entry += $" -> {body}";
            entry += Environment.NewLine;

            File.AppendAllText(file, entry, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error escribiendo log: {ex.Message}");
        }
    }

    static void EnsureLogsFolderExists()
    {
        try
        {
            string execFolder = AppContext.BaseDirectory;
            string projectRoot = Path.GetFullPath(Path.Combine(execFolder, "..", "..", ".."));
            string logsFolder = Path.Combine(projectRoot, "logs");
            Directory.CreateDirectory(logsFolder);
        }
        catch { }
    }
}
