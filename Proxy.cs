using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace proxy
{
    public class Proxy
    {
        public const int DataChunkSize = 8192;  
        public static IPAddress localIP = IPAddress.Parse("127.0.0.1");  
        public static int proxyPort = 8888;  

        // Чтение данных из сетевого потока, клиент отравляет запрос
        public static byte[] ReadRequestData(NetworkStream dataStream)
        {
            byte[] tempStorage = new byte[DataChunkSize];  
            byte[] resultData = new byte[DataChunkSize];    
            int totalBytes = 0, bytesRead;  

            do
            {
                // Чтение порции данных из потока
                bytesRead = dataStream.Read(tempStorage, 0, tempStorage.Length);

                Buffer.BlockCopy(
                    src: tempStorage,     
                    srcOffset: 0,        
                    dst: resultData,      
                    dstOffset: totalBytes,
                    count: bytesRead    
                );

                totalBytes += bytesRead;  

            } while (totalBytes < DataChunkSize && dataStream.DataAvailable);  

            return resultData.Take(totalBytes).ToArray();  
        }

        // Обработка нового клиентского подключения
        public static void HandleClientConnection(Socket connection)
        {
            NetworkStream clientChannel = new NetworkStream(connection);

            byte[] requestData = ReadRequestData(clientChannel);

            ProcessClientRequest(clientChannel, requestData);

            connection.Close();
        }

        // Основная логика обработки запроса
        public static void ProcessClientRequest(NetworkStream clientChannel, byte[] requestContent)
        {
            Socket targetServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            string targetHost = null; 
            try
            {
                string requestText = Encoding.UTF8.GetString(requestContent);
                IPEndPoint serverEndpoint = ResolveServerAddress(requestText, out targetHost);
                string modifiedRequest = AdjustRequestUrl(requestText);
                string absoluteUri = BuildAbsoluteUri(requestText, targetHost);
                targetServer.Connect(serverEndpoint);

                NetworkStream serverChannel = new NetworkStream(targetServer);

                byte[] processedRequest = Encoding.UTF8.GetBytes(modifiedRequest);
                serverChannel.Write(processedRequest, 0, processedRequest.Length);

                byte[] responseData = ReadRequestData(serverChannel);
                clientChannel.Write(responseData, 0, responseData.Length);

                LogTransactionDetails(responseData, absoluteUri);

                serverChannel.CopyTo(clientChannel);
            }
            catch (Exception ex)
            {
                if (ex is SocketException || ex is ArgumentException)
                {
                    SendErrorResponse(clientChannel, targetHost ?? "unknown");
                }
                return;
            }
            finally
            {
                targetServer.Close();  
            }
        }

        // Построение полного URL из запроса
        public static string BuildAbsoluteUri(string httpRequest, string hostname)
        {
            string[] requestLines = httpRequest.Split('\n');
            if (requestLines.Length == 0) return hostname;

            string[] requestParts = requestLines[0].Split(' ');
            if (requestParts.Length < 2) return hostname;

            string resourcePath = requestParts[1];

            if (resourcePath.StartsWith("http://") || resourcePath.StartsWith("https://"))
            {
                return resourcePath.Split(' ')[0];  
            }

            return "http://" + hostname + resourcePath;
        }

        // Логирование деталей запроса
        public static void LogTransactionDetails(byte[] serverResponse, string fullUrl)
        {
            string responseText = Encoding.UTF8.GetString(serverResponse);

            string[] responseLines = responseText.Split('\r', '\n');

            string statusCode = responseLines[0].Substring(responseLines[0].IndexOf(" ") + 1);

            Console.WriteLine(DateTime.Now + " " + fullUrl + " " + statusCode);
        }

        // Модификация запроса: удаление полного URL
        public static string AdjustRequestUrl(string rawRequest)
        {
            Regex pattern = new Regex(@"http:\/\/[a-z0-9а-я\.\:]*");
            Match hostMatch = pattern.Match(rawRequest);
            string domain = hostMatch.Value;

            return rawRequest.Replace(domain, "");
        }

        // Определение адреса целевого сервера
        public static IPEndPoint ResolveServerAddress(string httpRequest, out string targetHost)
        {
            Regex hostPattern = new Regex(@"Host: (((?<host>.+?):(?<port>\d+?))|(?<host>.+?))\s+",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

            Match matchedHost = hostPattern.Match(httpRequest);

            targetHost = matchedHost.Groups["host"].Value;
            int serverPort = 0;

            if (!int.TryParse(matchedHost.Groups["port"].Value, out serverPort))
            {
                serverPort = 80;
            }

            IPAddress serverIP = Dns.GetHostEntry(targetHost).AddressList[0];

            return new IPEndPoint(serverIP, serverPort);
        }

        // Отправка страницы с ошибкой
        public static void SendErrorResponse(NetworkStream targetStream, string domainName)
        {
            string errorHeader = $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/html\r\nContent-Length: ";
            string htmlBody = "<p>" + domainName;

            // Читаем файл с HTML-ошибкой
            using (var errorPage = File.OpenRead("error_page.html"))
            {
                byte[] fileData = new byte[errorPage.Length];
                errorPage.Read(fileData, 0, fileData.Length);

                string fullHeader = $"{errorHeader}{fileData.Length + htmlBody.Length}\r\n\r\n{htmlBody}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(fullHeader);

                byte[] response = new byte[headerBytes.Length + fileData.Length];

                Array.Copy(headerBytes, 0, response, 0, headerBytes.Length);
                Array.Copy(fileData, 0, response, headerBytes.Length, fileData.Length);

                targetStream.Write(response, 0, response.Length);
            }
        }

        // Запуск прокси-сервера
        public static void Run()
        {
            TcpListener mainListener = new TcpListener(localIP, proxyPort);
            mainListener.Start();

            while (true)
            {
                // Принимаем новое подключение
                Socket connection = mainListener.AcceptSocket();

                // Запускаем обработку в отдельном потоке
                Thread worker = new Thread(() => HandleClientConnection(connection));
                worker.Start();
            }
        }
    }
}