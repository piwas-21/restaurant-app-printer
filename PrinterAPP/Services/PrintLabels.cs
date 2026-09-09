namespace PrinterAPP.Services;

/// <summary>
/// Every fixed string a composed ticket can carry, in one language. Order CONTENT (item names,
/// ingredient names, addresses, notes) is data and is printed exactly as the feed payload carries
/// it; only these scaffolding words are translated. See <see cref="PrintLabelCatalog"/> for the
/// languages and <see cref="PrintLanguagePolicy"/> for how the venue's setting resolves to one.
/// </summary>
public sealed record PrintLabels(
    string OnlineOrder,
    string Type,
    string Table,
    string Customer,
    string Tel,
    string Notes,
    string Note,
    string NoPrefix,
    string ExtraPrefix,
    string SelectedPrefix,
    string Subtotal,
    string Tax,
    string Discount,
    string CustomerDiscount,
    string Promo,
    string DeliveryFee,
    string Tip,
    string Total,
    string Payment,
    string Paid,
    string Due,
    string DeliveryTo,
    string Instructions,
    string ThankYou,
    string DineIn,
    string TakeAway,
    string Delivery,
    string NoItems)
{
    /// <summary>
    /// Renders the raw <c>Order.Type</c> value the backend sends (DineIn/TakeAway/Delivery) in this
    /// language. An unknown value prints verbatim rather than lying about what the order is.
    /// </summary>
    public string OrderType(string rawType) => rawType switch
    {
        "DineIn" => DineIn,
        "TakeAway" => TakeAway,
        "Delivery" => Delivery,
        _ => rawType,
    };
}

/// <summary>
/// The fixed-string catalog, one <see cref="PrintLabels"/> per language the receipt codepage (PC857,
/// ADR-002) can actually render — the Latin-script European languages of the backend's ten locales.
/// Arabic/Russian/Chinese are deliberately ABSENT: PC857 cannot encode those scripts, and printing
/// one of them would put replacement garbage on paper where a word should be. A plain compiled
/// catalog rather than .resx: the receipt composer is source-linked into the plain net10.0 test
/// project, where resx satellite plumbing would not follow it, and ticket labels change with a
/// recompile either way.
/// </summary>
public static class PrintLabelCatalog
{
    /// <summary>
    /// The label set for <paramref name="languageCode"/>; anything unknown — including null — falls
    /// back to English, which is also what every existing install prints until it opts in.
    /// </summary>
    public static PrintLabels For(string? languageCode) => languageCode?.Trim().ToLowerInvariant() switch
    {
        "de" => German,
        "fr" => French,
        "it" => Italian,
        "es" => Spanish,
        "nl" => Dutch,
        "tr" => Turkish,
        _ => English,
    };

    public static readonly PrintLabels English = new(
        OnlineOrder: "ONLINE ORDER",
        Type: "Type",
        Table: "Table",
        Customer: "Customer",
        Tel: "Tel",
        Notes: "NOTES",
        Note: "NOTE",
        NoPrefix: "NO",
        ExtraPrefix: "+ EXTRA",
        SelectedPrefix: "+",
        Subtotal: "Subtotal",
        Tax: "Tax",
        Discount: "Discount",
        CustomerDiscount: "Customer discount",
        Promo: "Promo",
        DeliveryFee: "Delivery Fee",
        Tip: "Tip",
        Total: "TOTAL",
        Payment: "PAYMENT",
        Paid: "PAID",
        Due: "DUE",
        DeliveryTo: "DELIVERY TO",
        Instructions: "Instructions",
        ThankYou: "Thank you for your visit!",
        DineIn: "Dine-in",
        TakeAway: "Takeaway",
        Delivery: "Delivery",
        NoItems: "(No items in order)");

    public static readonly PrintLabels German = new(
        OnlineOrder: "ONLINE-BESTELLUNG",
        Type: "Typ",
        Table: "Tisch",
        Customer: "Kunde",
        Tel: "Tel",
        Notes: "NOTIZEN",
        Note: "NOTIZ",
        NoPrefix: "OHNE",
        ExtraPrefix: "+ EXTRA",
        SelectedPrefix: "+",
        Subtotal: "Zwischensumme",
        Tax: "MwSt",
        Discount: "Rabatt",
        CustomerDiscount: "Kundenrabatt",
        Promo: "Promo",
        DeliveryFee: "Liefergebühr",
        Tip: "Trinkgeld",
        Total: "TOTAL",
        Payment: "ZAHLUNG",
        Paid: "BEZAHLT",
        Due: "OFFEN",
        DeliveryTo: "LIEFERUNG AN",
        Instructions: "Hinweise",
        ThankYou: "Vielen Dank für Ihren Besuch!",
        DineIn: "Im Lokal",
        TakeAway: "Mitnehmen",
        Delivery: "Lieferung",
        NoItems: "(Keine Artikel in der Bestellung)");

    public static readonly PrintLabels French = new(
        OnlineOrder: "COMMANDE EN LIGNE",
        Type: "Type",
        Table: "Table",
        Customer: "Client",
        Tel: "Tél",
        Notes: "REMARQUES",
        Note: "REMARQUE",
        NoPrefix: "SANS",
        ExtraPrefix: "+ SUPPL",
        SelectedPrefix: "+",
        Subtotal: "Sous-total",
        Tax: "TVA",
        Discount: "Remise",
        CustomerDiscount: "Remise client",
        Promo: "Promo",
        DeliveryFee: "Frais de livraison",
        Tip: "Pourboire",
        Total: "TOTAL",
        Payment: "PAIEMENT",
        Paid: "PAYÉ",
        Due: "DÛ",
        DeliveryTo: "LIVRAISON À",
        Instructions: "Instructions",
        ThankYou: "Merci de votre visite !",
        DineIn: "Sur place",
        TakeAway: "À emporter",
        Delivery: "Livraison",
        NoItems: "(Aucun article dans la commande)");

    public static readonly PrintLabels Italian = new(
        OnlineOrder: "ORDINE ONLINE",
        Type: "Tipo",
        Table: "Tavolo",
        Customer: "Cliente",
        Tel: "Tel",
        Notes: "NOTE",
        Note: "NOTA",
        NoPrefix: "SENZA",
        ExtraPrefix: "+ EXTRA",
        SelectedPrefix: "+",
        Subtotal: "Subtotale",
        Tax: "IVA",
        Discount: "Sconto",
        CustomerDiscount: "Sconto cliente",
        Promo: "Promo",
        DeliveryFee: "Costo di consegna",
        Tip: "Mancia",
        Total: "TOTALE",
        Payment: "PAGAMENTO",
        Paid: "PAGATO",
        Due: "DA PAGARE",
        DeliveryTo: "CONSEGNA A",
        Instructions: "Istruzioni",
        ThankYou: "Grazie per la visita!",
        DineIn: "Al tavolo",
        TakeAway: "Da asporto",
        Delivery: "Consegna",
        NoItems: "(Nessun articolo nell'ordine)");

    public static readonly PrintLabels Spanish = new(
        OnlineOrder: "PEDIDO ONLINE",
        Type: "Tipo",
        Table: "Mesa",
        Customer: "Cliente",
        Tel: "Tel",
        Notes: "NOTAS",
        Note: "NOTA",
        NoPrefix: "SIN",
        ExtraPrefix: "+ EXTRA",
        SelectedPrefix: "+",
        Subtotal: "Subtotal",
        Tax: "IVA",
        Discount: "Descuento",
        CustomerDiscount: "Descuento cliente",
        Promo: "Promo",
        DeliveryFee: "Gastos de envío",
        Tip: "Propina",
        Total: "TOTAL",
        Payment: "PAGO",
        Paid: "PAGADO",
        Due: "PENDIENTE",
        DeliveryTo: "ENTREGAR EN",
        Instructions: "Indicaciones",
        ThankYou: "¡Gracias por su visita!",
        DineIn: "En el local",
        TakeAway: "Para llevar",
        Delivery: "Entrega",
        NoItems: "(Sin artículos en el pedido)");

    public static readonly PrintLabels Dutch = new(
        OnlineOrder: "ONLINE BESTELLING",
        Type: "Type",
        Table: "Tafel",
        Customer: "Klant",
        Tel: "Tel",
        Notes: "OPMERKINGEN",
        Note: "LET OP",
        NoPrefix: "ZONDER",
        ExtraPrefix: "+ EXTRA",
        SelectedPrefix: "+",
        Subtotal: "Subtotaal",
        Tax: "BTW",
        Discount: "Korting",
        CustomerDiscount: "Klantkorting",
        Promo: "Promo",
        DeliveryFee: "Bezorgkosten",
        Tip: "Fooi",
        Total: "TOTAAL",
        Payment: "BETALING",
        Paid: "BETAALD",
        Due: "OPENSTAAND",
        DeliveryTo: "BEZORGEN AAN",
        Instructions: "Instructies",
        ThankYou: "Bedankt voor uw bezoek!",
        DineIn: "Ter plaatse",
        TakeAway: "Meenemen",
        Delivery: "Bezorging",
        NoItems: "(Geen artikelen in de bestelling)");

    public static readonly PrintLabels Turkish = new(
        OnlineOrder: "ONLINE SİPARİŞ",
        Type: "Tür",
        Table: "Masa",
        Customer: "Müşteri",
        Tel: "Tel",
        Notes: "NOTLAR",
        Note: "NOT",
        NoPrefix: "YOK",
        ExtraPrefix: "+ EKSTRA",
        SelectedPrefix: "+",
        Subtotal: "Ara Toplam",
        Tax: "KDV",
        Discount: "İndirim",
        CustomerDiscount: "Müşteri indirimi",
        Promo: "Promosyon",
        DeliveryFee: "Teslimat Ücreti",
        Tip: "Bahşiş",
        Total: "TOPLAM",
        Payment: "ÖDEME",
        Paid: "ÖDENEN",
        Due: "KALAN",
        DeliveryTo: "TESLİMAT ADRESİ",
        Instructions: "Talimatlar",
        ThankYou: "Ziyaretiniz için teşekkürler!",
        DineIn: "Lokalda",
        TakeAway: "Paket",
        Delivery: "Teslimat",
        NoItems: "(Siparişte ürün yok)");
}
