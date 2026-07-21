using System.Reflection;

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.1");
    return 0;
}

Console.Error.WriteLine("The BlueTusk CLI is scaffolded but has no commands in milestone 0.0.1.");
return 2;
