using System.Windows.Markup;
using Eule.Core.Localization;

namespace Eule.App;

/// <summary>XAML text lookup: <c>Text="{l:T set.title}"</c>. Resolved once when the view loads.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
