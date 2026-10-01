namespace PrinterAPP.Services;

/// <summary>Receipt scaffolding in the same seven PC857-compatible languages as PrintLabelCatalog.</summary>
public sealed record MarketplacePrintLabels(string TestOrder, string PaymentHandledBy, string TaxNotReported)
{
    public static MarketplacePrintLabels For(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "de" => new("TESTBESTELLUNG", "Zahlung über {0}", "Vom Anbieter nicht gemeldet"),
        "fr" => new("COMMANDE DE TEST", "Paiement géré par {0}", "Non communiqué par le prestataire"),
        "it" => new("ORDINE DI PROVA", "Pagamento gestito da {0}", "Non comunicata dal fornitore"),
        "es" => new("PEDIDO DE PRUEBA", "Pago gestionado por {0}", "No informado por el proveedor"),
        "nl" => new("TESTBESTELLING", "Betaling verwerkt door {0}", "Niet gemeld door de aanbieder"),
        "tr" => new("TEST SİPARİŞİ", "Ödeme {0} tarafından yönetilir", "Sağlayıcı bildirmedi"),
        _ => new("TEST ORDER", "Payment handled by {0}", "Not reported by provider"),
    };
}
