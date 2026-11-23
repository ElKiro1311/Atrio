using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atrio.OptiScaler
{
    /// <summary>Something the person has to know or decide, told to Atrio with a code it can put into words.</summary>
    internal sealed class ServiceException : Exception
    {
        public string Code { get; }

        public ServiceException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// The conversation with Atrio: one JSON message per line through a pipe. Each question has a number and gets one
    /// answer with it, a result or an error; while a long one runs, notes about its progress come in between.
    /// </summary>
    internal sealed class Service
    {
        /// <summary>The version of these messages: Atrio and the bridge always travel together, so it's only a check.</summary>
        public const int Protocol = 1;

        private static readonly UTF8Encoding utf8 = new UTF8Encoding(false);
        private readonly object writing = new object();
        private readonly StreamWriter writer;
        private readonly StreamReader reader;
        private readonly Games games;
        private int current;
        private volatile bool gone;

        private Service(Stream stream)
        {
            reader = new StreamReader(stream, utf8);
            writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" };
            games = new Games(Progress);
        }

        /// <summary>Answers until Atrio closes its side. Anything that goes wrong in one question stays in that question.</summary>
        public static int Run(string pipe, string token)
        {
            if (string.IsNullOrEmpty(pipe))
            {
                return 2;
            }

            using (var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stream.Connect(10000);
                var service = new Service(stream);
                service.Send(new JsonObject { ["hello"] = token, ["protocol"] = Protocol });
                service.Loop();
            }

            return 0;
        }

        private void Loop()
        {
            string line;
            while ((line = Read()) != null)
            {
                JsonObject question;
                try
                {
                    question = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (question == null)
                {
                    continue;
                }

                current = (int?)question["id"] ?? 0;
                var answer = new JsonObject { ["id"] = current };
                try
                {
                    answer["result"] = Answer((string)question["method"], question["params"] as JsonObject ?? new JsonObject());
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    answer["error"] = Describe(e);
                }

                Send(answer);
                if ((string)question["method"] == "close")
                {
                    return;
                }
            }
        }

        private string Read()
        {
            try
            {
                return reader.ReadLine();
            }
            catch (IOException)
            {
                // Atrio is gone.
                return null;
            }
        }

        private void Send(JsonObject message)
        {
            lock (writing)
            {
                try
                {
                    writer.WriteLine(message.ToJsonString());
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    // Atrio is gone, or it asked to stop by closing its side. The next read ends the loop.
                    gone = true;
                }
            }
        }

        /// <summary>False when there's nobody on the other side anymore.</summary>
        private bool Progress(string step, string item, double? percent)
        {
            Send(new JsonObject
            {
                ["id"] = current,
                ["progress"] = new JsonObject { ["step"] = step, ["item"] = item, ["percent"] = percent }
            });
            return !gone;
        }

        private JsonNode Change(Func<JsonNode> action)
        {
            Progress("look", null, null);
            Games.CheckNoWindow();
            return Alone(action);
        }

        private static JsonNode Alone(Func<JsonNode> action)
        {
            JsonNode result = null;
            if (!Safety.Alone(TimeSpan.FromSeconds(90), () => result = action()))
            {
                throw new ServiceException("clientbusy", "Another program of the same kind is still working.");
            }

            return result;
        }

        private static JsonObject Describe(Exception exception)
        {
            // The classes of the client are known by their name: they can't be compiled against.
            var code = (exception as ServiceException)?.Code;
            if (code == null)
            {
                switch (exception.GetType().Name)
                {
                    case nameof(NotInThisVersionException):
                        code = "version";
                        break;
                    case "GitHubRateLimitException":
                        code = "limit";
                        break;
                    case "VersionUnavailableException":
                        code = "unavailable";
                        break;
                    case "DlssgSm86ProxyCollisionException":
                        code = "injection";
                        break;
                    case nameof(StoppedException):
                        code = "stopped";
                        break;
                    case nameof(UnauthorizedAccessException):
                        code = "access";
                        break;
                    case "HttpRequestException":
                        code = "network";
                        break;
                    default:
                        code = exception.InnerException is UnauthorizedAccessException ? "access" : "failed";
                        break;
                }
            }

            return new JsonObject { ["code"] = code, ["message"] = exception.Message, ["type"] = exception.GetType().Name };
        }

        private JsonNode Answer(string method, JsonObject parameters)
        {
            switch (method)
            {
                case "hello":
                    return Contract.Describe();
                case "state":
                    // After something that failed, the program that was left alone may still be at it.
                    return (bool?)parameters["alone"] == true ? Alone(() => games.State(parameters)) : games.State(parameters);
                case "refresh":
                    return games.Refresh();
                case "install":
                    return Change(() => games.Install(parameters));
                case "apply":
                    return Change(() => games.Apply(parameters));
                case "uninstall":
                    return Change(() => games.Uninstall(parameters));
                case "close":
                    return new JsonObject();
                default:
                    throw new ServiceException("method", $"Unknown question: {method}");
            }
        }

        public static JsonArray Array(IEnumerable<string> values)
        {
            return new JsonArray(values.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
        }
    }
}
