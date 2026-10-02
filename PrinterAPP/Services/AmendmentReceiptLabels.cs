namespace PrinterAPP.Services;

internal sealed record AmendmentReceiptLabels(string Amendment, string SourceOrder, string PrepareThisTicket)
{
    internal static AmendmentReceiptLabels For(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "de" => new("Änderung", "Ursprüngliche Bestellung", "Nur die Artikel auf diesem Bon zubereiten."),
        "fr" => new("Modification", "Commande d'origine", "Préparer uniquement les articles de ce ticket."),
        "it" => new("Modifica", "Ordine originale", "Preparare solo gli articoli su questo scontrino."),
        "es" => new("Modificación", "Pedido original", "Preparar solo los artículos de este ticket."),
        "nl" => new("Wijziging", "Oorspronkelijke bestelling", "Bereid alleen de artikelen op deze bon."),
        "tr" => new("Değişiklik", "İlk sipariş", "Yalnızca bu fişteki ürünleri hazırlayın."),
        _ => new("Amendment", "Source order", "Prepare only the items on this ticket.")
    };
}
