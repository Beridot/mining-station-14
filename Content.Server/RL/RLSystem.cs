using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Log;

public class RLSystem : EntitySystem
{
    private string entryPoint = "Resources/Mining/RL/init.lisp";

    private bool available = false;

    public override void Initialize()
    {
#if RL
        try
        {
            base.Initialize();
            RL.ecl_set_option(1, 0); // TRAP_SIGSEGV = false
            RL.ecl_set_option(2, 0); // TRAP_SIGFPE = false
            RL.ecl_set_option(3, 0); // TRAP_SIGINT = false
            RL.ecl_set_option(4, 0); // TRAP_SIGILL = false
            RL.ecl_set_option(5, 0); // TRAP_SIGBUS = false
            RL.ecl_set_option(6, 0); // TRAP_SIGPIPE = false
            if (RL.boot() == 0)
            {
                throw new Exception("Failed to start; could not boot");
            }
            Logger.DebugS("RL", "Started RL");
            available = true;
            Reload();
        }
        catch (TypeInitializationException e)
        {
            Logger.ErrorS("RL", "Failed to start; could not find libRL");
        }
#else
        Logger.ErrorS("RL", "Not available in this build");
#endif
    }

    public bool Available()
    {
        return available;
    }

    public string Reload()
    {
#if RL
        return RL.repr(RL.eval_str($"(load \"{entryPoint}\")"));
#else
        return "Not available in this build";
#endif
    }

    public override void Shutdown()
    {
        base.Shutdown();
#if RL
        RL.cl_shutdown();
#endif
    }
}

#if RL
[AdminCommand(AdminFlags.Server)]
sealed class RLReloadCommand : IConsoleCommand
{
    public string Command => "reload";
    public string Description => "Reload RL";
    public string Help => "reload";
    
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var sysMan = IoCManager.Resolve<IEntitySystemManager>();
        var _rl = sysMan.GetEntitySystem<RLSystem>();
        if (!_rl.Available())
        {
            shell.WriteLine("RL is not available");
            return;
        }
        shell.WriteLine(_rl.Reload());
    }
}

[AdminCommand(AdminFlags.Query)]
sealed class RLEvalCommand : IConsoleCommand
{
    public string Command => "eval";
    public string Description => "Evaluate RL expression";
    public string Help => "eval <expression>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteLine(Help);
            return;
        }

        var sysMan = IoCManager.Resolve<IEntitySystemManager>();
        var _rl = sysMan.GetEntitySystem<RLSystem>();
        if (!_rl.Available())
        {
            shell.WriteLine("RL is not available");
            return;
        }

        var result = RL.eval_str(args[0]);
        shell.WriteLine(RL.repr(result));
    }
}

[AdminCommand(AdminFlags.Query)]
sealed class RLDebugCommand : IConsoleCommand
{
    public string Command => "rldebug";
    public string Description => "Enable RL debugging";
    public string Help => "Enable RL debugging";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        RL.set_debug(1);
    }
}
#endif
