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

        [Fact]
        public void TestModConfigManagerParsesCustomDirectoriesAndStartupMods()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "BepinExJSTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // Create custom directories
                var bepinexJsDir = Path.Combine(tempDir, "BepInExJS");
                var myModsDir = Path.Combine(tempDir, "MyMods");
                Directory.CreateDirectory(bepinexJsDir);
                Directory.CreateDirectory(myModsDir);

                // Create some mod files
                File.WriteAllText(Path.Combine(bepinexJsDir, "auto1.js"), "console.log('auto1');");
                File.WriteAllText(Path.Combine(myModsDir, "custom1.js"), "console.log('custom1');");
                File.WriteAllText(Path.Combine(myModsDir, "disabled.js"), "console.log('disabled');");

                // Write BepInExJS.json
                var jsonContent = @"{
                  ""enabled"": true,
                  ""port"": 9999,
                  ""autoloadDirectories"": [
                    ""BepInExJS""
                  ],
                  ""startupMods"": [
                    ""MyMods/custom1.js"",
                    { ""path"": ""MyMods/disabled.js"", ""enabled"": false }
                  ]
                }";
                File.WriteAllText(Path.Combine(tempDir, "BepInExJS.json"), jsonContent);

                var logger = new ManualLogSource("TestLog");
                var config = new ModConfigManager(tempDir, logger);

                Assert.True(config.Enabled);
                Assert.Equal(9999, config.Port);

                var resolved = new List<string>(config.ResolveAllStartupModFiles());
                Assert.Contains(resolved, f => f.EndsWith("auto1.js", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(resolved, f => f.EndsWith("custom1.js", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resolved, f => f.EndsWith("disabled.js", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestActionAndUnityActionSupport()
        {
            var logs = new List<string>();
            var runtime = new JsRuntimeManager(new ManualLogSource("Test"), (l, m) => logs.Add(m));

            var script = @"
                let actionTriggered = false;
                let unityActionTriggered = false;
                let receivedVal = 0;

                let myAction = Action(() => {
                    actionTriggered = true;
                    console.log('Action triggered!');
                });

                let myUnityAction = UnityAction(() => {
                    unityActionTriggered = true;
                    console.log('UnityAction triggered!');
                });

                let myValAction = toAction1((val) => {
                    receivedVal = val;
                    console.log('Received val:', val);
                });

                // Invoke the C# delegates
                myAction();
                myUnityAction();
                myValAction(42);
            ";

            runtime.Reload(script, "actions.js");

            Assert.Contains(logs, l => l.Contains("Action triggered!"));
            Assert.Contains(logs, l => l.Contains("UnityAction triggered!"));
            Assert.Contains(logs, l => l.Contains("Received val: 42"));
        }

        [Fact]
        public void TestGetTypeHelperAndMultiAssemblyTypeGen()
        {
            using var runtime = new JsRuntimeManager(new ManualLogSource("TestLog"), (lvl, msg) => { });

            // Test getType helper with various objects
            var res1 = runtime.ExecuteRepl("getType(CS.System.DateTime.UtcNow)");
            Assert.Contains("System.DateTime", res1);

            var res2 = runtime.ExecuteRepl("getType(new CS.System.Text.StringBuilder())");
            Assert.Contains("System.Text.StringBuilder", res2);

            // Test multi-assembly type definition generation
            var dts = TypeDefGenerator.GenerateDtsForLoadedAssemblies("mscorlib", "System");
            Assert.Contains("namespace System", dts);
            Assert.Contains("namespace CS", dts);
        }

        [Fact]
        public void TestSelfContainedModFolderAndDirname()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "BepinExJS_SelfContained_" + Guid.NewGuid().ToString("N"));
            var modDir = Path.Combine(tempDir, "BepInExJS", "CoolMod");
            Directory.CreateDirectory(modDir);

            try
            {
                var modJson = @"{ ""name"": ""CoolMod"", ""main"": ""entry.js"" }";
                File.WriteAllText(Path.Combine(modDir, "mod.json"), modJson);
                File.WriteAllText(Path.Combine(modDir, "entry.js"), @"
                    console.log('DIR:' + __dirname);
                    console.log('FILE:' + __filename);
                    console.log('RESOLVE:' + resolvePath('assets/data.json'));
                ");

                // Write config
                var configJson = @"{ ""autoloadDirectories"": [""BepInExJS""] }";
                File.WriteAllText(Path.Combine(tempDir, "BepInExJS.json"), configJson);

                var config = new ModConfigManager(tempDir, new ManualLogSource("TestLog"));
                var resolved = new List<string>(config.ResolveAllStartupModFiles());
                Assert.Single(resolved);
                Assert.Contains("entry.js", resolved[0]);

                // Test runtime injection of __dirname and resolvePath
                var logs = new List<string>();
                using var runtime = new JsRuntimeManager(new ManualLogSource("TestLog"), (lvl, msg) => logs.Add(msg));

                runtime.ExecuteStartupScript(File.ReadAllText(resolved[0]), resolved[0], Path.GetDirectoryName(resolved[0]));

                Assert.Contains(logs, l => l.Contains("DIR:" + modDir));
                Assert.Contains(logs, l => l.Contains("FILE:" + resolved[0]));
                Assert.Contains(logs, l => l.Contains("RESOLVE:" + Path.Combine(modDir, "assets", "data.json")));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestWsServerPortProbingAndSessionRegistry()
        {
            var logger = new ManualLogSource("TestLog");

            // Start server 1 on preferred port 9290
            using var server1 = new WsServer("127.0.0.1", 9290, (msg, socket) => { }, msg => { });
            Assert.Equal(9290, server1.BoundPort);

            // Start server 2 with preferred port 9290 (should probe and bind 9291)
            using var server2 = new WsServer("127.0.0.1", 9290, (msg, socket) => { }, msg => { });
            Assert.Equal(9291, server2.BoundPort);

            // Test GameSessionRegistry
            using var registry = new GameSessionRegistry(logger);
            registry.RegisterSession(server1.BoundPort);

            var sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".bepinexjs",
                "sessions"
            );
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var sessionFile = Path.Combine(sessionsDir, $"{pid}.json");

            Assert.True(File.Exists(sessionFile));
            var content = File.ReadAllText(sessionFile);
            Assert.Contains(server1.BoundPort.ToString(), content);
        }

        [Fact]
        public void TestCrossGameEventHandling()
        {
            var logger = new ManualLogSource("TestLog");
            var logs = new List<string>();

            using var runtime = new JsRuntimeManager(logger, (lvl, msg) => logs.Add(msg));

            var script = @"
                let receivedMsg = '';
                let receivedPid = 0;

                CrossGame.on('boss_slain', (data, sourcePid) => {
                    receivedMsg = data.boss;
                    receivedPid = sourcePid;
                    console.log('EVENT_RECEIVED:' + data.boss + '_FROM_' + sourcePid);
                });
            ";

            runtime.Reload(script, "crossgame_test.js");

            // Dispatch an event as if received from another game
            runtime.CrossGame?.HandleIncomingEvent("boss_slain", "{\"boss\":\"Mithrix\"}", 54321);

            Assert.Contains(logs, l => l.Contains("EVENT_RECEIVED:Mithrix_FROM_54321"));
        }

        [Fact]
        public void TestModularAssemblyTypeDefGeneration()
        {
            // Test per-assembly type generation using mscorlib / System
            var perAsm = TypeDefGenerator.GenerateDtsPerAssembly("mscorlib", "System");

            Assert.NotEmpty(perAsm);
            // Verify that each assembly entry has its own declare namespace CS
            foreach (var kvp in perAsm)
            {
                Assert.Contains("declare namespace CS {", kvp.Value);
                Assert.Contains($"// Auto-generated TypeScript definitions for Assembly: {kvp.Key}", kvp.Value);
            }

            // Test extracting ALL assemblies via "*"
            var allAsm = TypeDefGenerator.GenerateDtsPerAssembly("*");
            Assert.True(allAsm.Count >= perAsm.Count);
        }
    }
}

