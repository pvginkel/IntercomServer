namespace IntercomTest;

internal class ComboBoxItem(string label, object tag)
{
    public object Tag { get; } = tag;

    public override string ToString() => label;
}
