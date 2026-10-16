// KEDO command line. Commands are added as the language lands; see docs/plans.
if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("kedo <command> [options]");
    Console.WriteLine("  version   Print the KEDO toolchain version.");
    return 0;
}

if (args[0] == "version")
{
    Console.WriteLine(Kedo.KedoVersion.Current);
    return 0;
}

Console.Error.WriteLine($"Unknown command '{args[0]}'.");
return 2;
