using System.Windows.Input;
using GlDrive.Spread;

namespace GlDrive.UI;

/// <summary>Card actions use their explicit target; toolbar and keyboard actions use current selection.</summary>
internal sealed class SpreadRaceCommand(Func<SpreadJobVm?> selected, Action<SpreadJobVm> execute) : ICommand
{
    private SpreadJobVm? Target(object? parameter) => parameter is SpreadJobVm card ? card : parameter == null ? selected() : null;
    public bool CanExecute(object? parameter) => Target(parameter) is { IsFinished: false };
    public void Execute(object? parameter)
    {
        if (Target(parameter) is { IsFinished: false } target) execute(target);
    }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
