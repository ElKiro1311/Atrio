using Atrio.OptiScaler;
using System;
using System.Reflection;

/// <summary>
/// What the runtime of .NET calls before the program of OptiScaler Client starts, because Atrio asked for it with
/// DOTNET_STARTUP_HOOKS. The name, the place (no namespace) and the method are the ones .NET looks for.
/// The client itself isn't modified: without Atrio starting it, none of this exists for it.
/// </summary>
internal class StartupHook
{
    public const string ModeVariable = "ATRIO_OPTISCALER_MODE";
    public const string PipeVariable = "ATRIO_OPTISCALER_PIPE";
    public const string TokenVariable = "ATRIO_OPTISCALER_TOKEN";
    public const string GameVariable = "ATRIO_OPTISCALER_GAME";

    public static void Initialize()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        var pipe = Environment.GetEnvironmentVariable(PipeVariable);
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var game = Environment.GetEnvironmentVariable(GameVariable);

        // What the client starts (an installer, the folder of a game) must not get any of this.
        foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", ModeVariable, PipeVariable, TokenVariable, GameVariable })
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        var entry = Assembly.GetEntryAssembly();
        if (entry?.GetName().Name != Client.AssemblyName)
        {
            if (mode == "service")
            {
                // Another program, or one that changed its name: Atrio wanted to talk, not its window.
                Environment.Exit(3);
            }

            return;
        }

        Client.Assembly = entry;
        if (mode == "service")
        {
            var code = 1;
            try
            {
                code = Service.Run(pipe, token);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
            }

            // The program never gets to start: there is no window in this mode.
            Environment.Exit(code);
        }
        else if (mode == "window")
        {
            try
            {
                GameWindow.Prepare(game);
            }
            catch (Exception e)
            {
                // The client opens as always, on its list of games.
                Console.Error.WriteLine(e.Message);
            }
        }
    }
}
