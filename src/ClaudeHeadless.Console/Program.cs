// claude-headless.exe: status, run-queue and autostart, with output.
using System.Text;

if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
return ClaudeHeadless.HeadlessCli.Run(args, new ClaudeHeadless.HeadlessHost { LogEcho = Console.Out });
