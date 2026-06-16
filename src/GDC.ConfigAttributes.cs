namespace GDCplugin
{
    // Duck-typed by BepInEx's ConfigurationManager: it finds this by the type
    // name "ConfigurationManagerAttributes" (any namespace) among a setting's
    // ConfigDescription tags and reflects over these fields. Only the ones we
    // use are declared. Order is descending (higher = nearer the top of its
    // section); IsAdvanced hides a setting behind the "Advanced settings"
    // toggle so the common controls stay uncluttered.
    internal sealed class ConfigurationManagerAttributes
    {
        public int?    Order;
        public bool?   IsAdvanced;
        public bool?   Browsable;
        public bool?   ReadOnly;
        public string? Category;
    }
}
