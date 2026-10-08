namespace PrinterAPP.Services;

/// <summary>Receipt scaffolding in the same seven PC857-compatible languages as PrintLabelCatalog.</summary>
public sealed record MarketplacePrintLabels(string TestOrder, string PaymentHandledBy, string TaxNotReported, string PhoneAccessCode)
{
    public static MarketplacePrintLabels For(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "de" => new("TESTBESTELLUNG", "Zahlung über {0}", "Vom Anbieter nicht gemeldet", "Telefonzugangscode"),
        "fr" => new("COMMANDE DE TEST", "Paiement géré par {0}", "Non communiqué par le prestataire", "Code d'accès téléphonique"),
        "it" => new("ORDINE DI PROVA", "Pagamento gestito da {0}", "Non comunicata dal fornitore", "Codice di accesso telefonico"),
        "es" => new("PEDIDO DE PRUEBA", "Pago gestionado por {0}", "No informado por el proveedor", "Código de acceso telefónico"),
        "nl" => new("TESTBESTELLING", "Betaling verwerkt door {0}", "Niet gemeld door de aanbieder", "Toegangscode telefoon"),
        "tr" => new("TEST SİPARİŞİ", "Ödeme {0} tarafından yönetilir", "Sağlayıcı bildirmedi", "Telefon erişim kodu"),
        _ => new("TEST ORDER", "Payment handled by {0}", "Not reported by provider", "Phone access code"),
    };
}
