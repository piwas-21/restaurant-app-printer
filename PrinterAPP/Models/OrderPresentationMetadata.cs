namespace PrinterAPP.Models;

/// <summary>Quantity and composition metadata mirrored from the backend order projection.</summary>
public enum QuantityBasis
{
    PerParentUnit,
    LineTotal,
    Unknown,
}

/// <summary>Whether the frozen component configuration applies across parent units.</summary>
public enum ConfigurationScope
{
    SharedAcrossParentUnits,
    IndependentParentUnits,
    Unknown,
}

/// <summary>Frozen menu-composition role used to present components without name matching.</summary>
public enum CompositionRole
{
    Menu,
    Dish,
    RequiredChoice,
    Extra,
    Sauce,
    Side,
    Drink,
    Ingredient,
    Unknown,
}
