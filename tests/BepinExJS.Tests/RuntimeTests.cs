using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using BepinExJS.Plugin;
using Xunit;

namespace BepinExJS.Tests
{
    public class RuntimeTests
    {
        private readonly ManualLogSource _logger = new ManualLogSource("TestLogger");

        [Fact]
        public void TestJsRuntimeEvaluatesAndAccessesClr()
        {
            var logs = new List<string>();
            var runtime = new JsRuntimeManager(_logger, (level, msg) => logs.Add($"{level}: {msg}"));

            var jsCode = @"
                let a = 15;
                let b = 25;
                let max = CS.System.Math.Max(a, b);
                console.log('Calculated max:', max);
            ";

            runtime.Reload(jsCode, "test.js");

            Assert.Contains(logs, l => l.Contains("Calculated max: 25"));
        }

        [Fact]
        public void TestImportTypeExplicit()
        {
            var logs = new List<string>();
            var runtime = new JsRuntimeManager(_logger, (level, msg) => logs.Add($"{level}: {msg}"));

            var jsCode = @"
                let Guid = importType('System.Guid');
                let id = Guid.NewGuid().ToString();
                console.log('Generated GUID length:', id.length);
            ";

            runtime.Reload(jsCode, "test_guid.js");

            Assert.Contains(logs, l => l.Contains("Generated GUID length: 36"));
        }

        [Fact]
        public void TestLifecycleAndHotReloadUnloadHook()
        {
            var logs = new List<string>();
            var runtime = new JsRuntimeManager(_logger, (level, msg) => logs.Add($"{level}: {msg}"));

            var script1 = @"
                let counter = 0;
                onUpdate(() => {
                    counter++;
                });

                onUnload(() => {
                    console.log('Old script unloaded!');
                });
            ";

            runtime.Reload(script1, "v1.js");

            // Tick update
            runtime.TickUpdate();
            runtime.TickUpdate();

            var script2 = @"
                console.log('Script v2 loaded!');
            ";

            // Reload should trigger onUnload from script1
            runtime.Reload(script2, "v2.js");

            Assert.Contains(logs, l => l.Contains("Old script unloaded!"));
            Assert.Contains(logs, l => l.Contains("Script v2 loaded!"));
        }

        [Fact]
        public void TestReplExecution()
        {
            var runtime = new JsRuntimeManager(_logger, (level, msg) => { });

            var res1 = runtime.ExecuteRepl("100 * 4");
            Assert.Equal("400", res1);

            var res2 = runtime.ExecuteRepl("CS.System.DateTime.UtcNow.getUTCFullYear()");
            Assert.Equal(DateTime.UtcNow.Year.ToString(), res2);
        }

        [Fact]
        public async Task TestWsServerCommunication()
        {
            var receivedMessages = new List<string>();
            var tcs = new TaskCompletionSource<string>();

            using var server = new WsServer("127.0.0.1", 9192, (msg, socket) =>
            {
                receivedMessages.Add(msg);
                socket.Send("{\"type\":\"pong\"}");
                tcs.TrySetResult(msg);
            }, msg => { });

            // Connect client using System.Net.WebSockets
            using var client = new System.Net.WebSockets.ClientWebSocket();
            await client.ConnectAsync(new Uri("ws://127.0.0.1:9192"), System.Threading.CancellationToken.None);

            var sendBuffer = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await client.SendAsync(new ArraySegment<byte>(sendBuffer), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Equal(tcs.Task, completedTask);
            Assert.Contains("ping", receivedMessages[0]);

            var receiveBuffer = new byte[1024];
            var recvResult = await client.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), System.Threading.CancellationToken.None);
            var reply = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, recvResult.Count);
            Assert.Contains("pong", reply);

            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", System.Threading.CancellationToken.None);
        }
    }
}
