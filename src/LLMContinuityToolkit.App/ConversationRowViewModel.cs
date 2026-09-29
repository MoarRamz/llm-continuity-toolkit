using System.ComponentModel;
using LLMContinuityToolkit.Core;

namespace LLMContinuityToolkit.App;

public sealed class ConversationRowViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public ConversationRowViewModel(ConversationRecord record) => Record = record;
    public ConversationRecord Record { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
