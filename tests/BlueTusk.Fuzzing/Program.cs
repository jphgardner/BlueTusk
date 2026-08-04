using SharpFuzz;

namespace BlueTusk.Fuzzing;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--list-targets"])
        {
            foreach (var listedTarget in FuzzTargets.Names)
            {
                Console.WriteLine(listedTarget);
            }

            return 0;
        }

        if (args is ["--replay", var replayTarget, var path])
        {
            FuzzTargets.Run(replayTarget, File.ReadAllBytes(path));
            return 0;
        }

        var target = Environment.GetEnvironmentVariable("BLUETUSK_FUZZ_TARGET");
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                "Set BLUETUSK_FUZZ_TARGET to one of: " +
                string.Join(", ", FuzzTargets.Names));
            return 2;
        }

        Fuzzer.OutOfProcess.Run(stream =>
        {
            var input = ReadBoundedInput(stream);
            if (input is not null)
            {
                FuzzTargets.Run(target, input);
            }
        });
        return 0;
    }

    private static byte[]? ReadBoundedInput(Stream stream)
    {
        var input = new byte[FuzzTargets.MaximumInputBytes];
        var length = 0;
        while (length < input.Length)
        {
            var read = stream.Read(input, length, input.Length - length);
            if (read == 0)
            {
                Array.Resize(ref input, length);
                return input;
            }

            length += read;
        }

        return stream.ReadByte() == -1 ? input : null;
    }
}
