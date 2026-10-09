using System.Globalization;
using PrinterAPP.Models;

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
    string CardAtRestaurant,
    string Paid,
    string Due,
    string DeliveryTo,
    string Instructions,
    string ThankYou,
    string DineIn,
    string TakeAway,
    string Delivery,
    string NoItems,
    string PaymentTip)
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

    // Legacy feed payloads may still send the backend enum's numeric code instead of its name.

    /// <summary>
    /// Maps the on-site card intent without leaking a backend enum to paper. The feed normally
    /// sends <c>CreditCard</c>, but old installations may send the generic <c>Card</c> alias or
    /// the legacy numeric enum code; all known forms get the same label while unknown values
    /// remain visible.
    /// </summary>
    public string PaymentMethodLabel(string? rawMethod)
    {
        var normalized = (rawMethod ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        var isCardPayment = normalized is "creditcard" or "card"
            || normalized == ((int)PaymentMethodCode.CreditCard).ToString(CultureInfo.InvariantCulture);

        return isCardPayment ? CardAtRestaurant
            : rawMethod ?? string.Empty;
    }
}

/// <summary>
/// The fixed-string catalog, one <see cref="PrintLabels"/> per language the receipt codepage (PC857,
/// ADR-002) can actually render — the Latin-script European languages of the backend's ten locales.
/// Arabic/Russian/Chinese are deliberately ABSENT: PC857 cannot encode those scripts, and printing
/// one of them would put replacement garbage on paper where a word should be. A plain compiled
/// catalog rather than .resx: the receipt composer is source-linked into the plain net10.0 test
/// project, where resx satellite plumbing would not follow it, and ticket labels change with a
/// recompile either way.
/// <para>
/// Each language is ONE pipe-delimited spec rather than seven near-identical initializer blocks —
/// the block form was structurally duplicated text, which the Sonar new-code duplication gate
/// measures. <see cref="Parse"/> maps positions to the record's named properties; a spec with the
/// wrong field count fails the completeness test instead of silently shifting a label.
/// </para>
/// </summary>
public static class PrintLabelCatalog
{
    /// <summary>Field order of every spec below — matches the <see cref="PrintLabels"/> constructor.</summary>
    private static readonly string[] SpecOrder =
    [
        "OnlineOrder", "Type", "Table", "Customer", "Tel", "Notes", "Note", "NoPrefix",
        "ExtraPrefix", "SelectedPrefix", "Subtotal", "Tax", "Discount", "CustomerDiscount",
        "Promo", "DeliveryFee", "Tip", "Total", "Payment", "CardAtRestaurant", "Paid", "Due", "DeliveryTo",
        "Instructions", "ThankYou", "DineIn", "TakeAway", "Delivery", "NoItems", "PaymentTip",
    ];

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

    public static readonly PrintLabels English = Parse(
        "ONLINE ORDER|Type|Table|Customer|Tel|NOTES|NOTE|NO|+ EXTRA|+|Subtotal|Tax|Discount" +
        "|Customer discount|Promo|Delivery Fee|Tip|TOTAL|PAYMENT|CARD AT RESTAURANT|PAID|DUE|DELIVERY TO" +
        "|Instructions|Thank you for your visit!|Dine-in|Takeaway|Delivery|(No items in order)|Additional tip");

    public static readonly PrintLabels German = Parse(
        "ONLINE-BESTELLUNG|Typ|Tisch|Kunde|Tel|NOTIZEN|NOTIZ|OHNE|+ EXTRA|+|Zwischensumme|MwSt|Rabatt" +
        "|Kundenrabatt|Promo|Liefergebühr|Trinkgeld|TOTAL|ZAHLUNG|KARTENZAHLUNG IM RESTAURANT|BEZAHLT|OFFEN|LIEFERUNG AN" +
        "|Hinweise|Vielen Dank für Ihren Besuch!|Im Lokal|Mitnehmen|Lieferung|(Keine Artikel in der Bestellung)|Zusätzliches Trinkgeld");

    public static readonly PrintLabels French = Parse(
        "COMMANDE EN LIGNE|Type|Table|Client|Tél|REMARQUES|REMARQUE|SANS|+ SUPPL|+|Sous-total|TVA|Remise" +
        "|Remise client|Promo|Frais de livraison|Pourboire|TOTAL|PAIEMENT|CARTE AU RESTAURANT|PAYÉ|DÛ|LIVRAISON À" +
        "|Instructions|Merci de votre visite !|Sur place|À emporter|Livraison|(Aucun article dans la commande)|Pourboire supplémentaire");

    public static readonly PrintLabels Italian = Parse(
        "ORDINE ONLINE|Tipo|Tavolo|Cliente|Tel|NOTE|NOTA|SENZA|+ EXTRA|+|Subtotale|IVA|Sconto" +
        "|Sconto cliente|Promo|Costo di consegna|Mancia|TOTALE|PAGAMENTO|CARTA AL RISTORANTE|PAGATO|DA PAGARE|CONSEGNA A" +
        "|Istruzioni|Grazie per la visita!|Al tavolo|Da asporto|Consegna|(Nessun articolo nell'ordine)|Mancia aggiuntiva");

    public static readonly PrintLabels Spanish = Parse(
        "PEDIDO ONLINE|Tipo|Mesa|Cliente|Tel|NOTAS|NOTA|SIN|+ EXTRA|+|Subtotal|IVA|Descuento" +
        "|Descuento cliente|Promo|Gastos de envío|Propina|TOTAL|PAGO|TARJETA EN EL RESTAURANTE|PAGADO|PENDIENTE|ENTREGAR EN" +
        "|Indicaciones|¡Gracias por su visita!|En el local|Para llevar|Entrega|(Sin artículos en el pedido)|Propina adicional");

    public static readonly PrintLabels Dutch = Parse(
        "ONLINE BESTELLING|Type|Tafel|Klant|Tel|OPMERKINGEN|LET OP|ZONDER|+ EXTRA|+|Subtotaal|BTW|Korting" +
        "|Klantkorting|Promo|Bezorgkosten|Fooi|TOTAAL|BETALING|KAARTBETALING IN HET RESTAURANT|BETAALD|OPENSTAAND|BEZORGEN AAN" +
        "|Instructies|Bedankt voor uw bezoek!|Ter plaatse|Meenemen|Bezorging|(Geen artikelen in de bestelling)|Extra fooi");

    public static readonly PrintLabels Turkish = Parse(
        "ONLINE SİPARİŞ|Tür|Masa|Müşteri|Tel|NOTLAR|NOT|YOK|+ EKSTRA|+|Ara Toplam|KDV|İndirim" +
        "|Müşteri indirimi|Promosyon|Teslimat Ücreti|Bahşiş|TOPLAM|ÖDEME|RESTORANDA KARTLA ÖDEME|ÖDENEN|KALAN|TESLİMAT ADRESİ" +
        "|Talimatlar|Ziyaretiniz için teşekkürler!|Lokalda|Paket|Teslimat|(Siparişte ürün yok)|Ek bahşiş");

    /// <summary>Maps one spec onto the record; a short or long spec fails loudly here.</summary>
    private static PrintLabels Parse(string spec)
    {
        var fields = spec.Split('|');
        if (fields.Length != SpecOrder.Length)
        {
            throw new InvalidOperationException(
                $"PrintLabels spec has {fields.Length} fields, expected {SpecOrder.Length}.");
        }

        return new PrintLabels(
            OnlineOrder: fields[0],
            Type: fields[1],
            Table: fields[2],
            Customer: fields[3],
            Tel: fields[4],
            Notes: fields[5],
            Note: fields[6],
            NoPrefix: fields[7],
            ExtraPrefix: fields[8],
            SelectedPrefix: fields[9],
            Subtotal: fields[10],
            Tax: fields[11],
            Discount: fields[12],
            CustomerDiscount: fields[13],
            Promo: fields[14],
            DeliveryFee: fields[15],
            Tip: fields[16],
            Total: fields[17],
            Payment: fields[18],
            CardAtRestaurant: fields[19],
            Paid: fields[20],
            Due: fields[21],
            DeliveryTo: fields[22],
            Instructions: fields[23],
            ThankYou: fields[24],
            DineIn: fields[25],
            TakeAway: fields[26],
            Delivery: fields[27],
            NoItems: fields[28],
            PaymentTip: fields[29]);
    }
}
