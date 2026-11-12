// claude-headless-background.exe: no window, ever. What the run-queue task runs, every 15 minutes.
return ClaudeHeadless.HeadlessCli.Run(args, new ClaudeHeadless.HeadlessHost { Out = TextWriter.Null, Err = TextWriter.Null });
