namespace TapeDrop.Probe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ProbeArgs parsed;
        try
        {
            parsed = ProbeArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintHelp();
            return 2;
        }

        if (parsed.Has("help") || parsed.Command is "help")
        {
            PrintHelp();
            return 0;
        }

        var report = new ProbeReport();
        int code;
        try
        {
            code = parsed.Command switch
            {
                "scan" => RunScan(report),
                "read-test" => await SpeedTests.ReadTestAsync(parsed, report),
                "copy-test" => await SpeedTests.CopyTestAsync(parsed, report),
                _ => Unknown(parsed.Command),
            };
        }
        catch (Exception ex)
        {
            report.Line();
            report.Line("The probe stopped with an error: " + ex.Message);
            code = 1;
        }

        if (parsed.Command is "scan" or "read-test" or "copy-test")
        {
            var path = report.Save("tapedrop-probe-" + parsed.Command);
            Console.WriteLine();
            Console.WriteLine("Report saved to: " + path);
            Console.WriteLine("Send me the contents of that file.");
        }

        // Double-clicked from Explorer: keep the window open so the result can be read.
        if (args.Length == 0 && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }

        return code;
    }

    private static int RunScan(ProbeReport report)
    {
        SystemScan.Run(report);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            TapeDrop probe: checks a computer before TapeDrop is used with a real tape drive.

              tapedrop-probe                    scan this computer (read-only). Same as: scan
              tapedrop-probe read-test  --source <folder> [--gb 2]
                                                read and hash files from the USB RAID; writes nothing
              tapedrop-probe copy-test  --source <folder> --target <mounted tape> [--gb 2] [--verify] [--keep] [--yes]
                                                write a test copy to the target. Asks you to type YES first.

            Every command saves a text report you can send back.
            """);
    }
}
