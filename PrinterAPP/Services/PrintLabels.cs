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
    string PaymentTip,
    string Each,
    string PaymentState,
    string Unpaid,
    string PartiallyPaid,
    string Refunded,
    string Overpaid,
    string Credit)
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
        "SelectedPrefix", "Subtotal", "Tax", "Discount", "CustomerDiscount",
        "Promo", "DeliveryFee", "Tip", "Total", "Payment", "CardAtRestaurant", "Paid", "Due", "DeliveryTo",
        "Instructions", "ThankYou", "DineIn", "TakeAway", "Delivery", "NoItems", "PaymentTip",
        "Each", "PaymentState", "Unpaid", "PartiallyPaid", "Refunded", "Overpaid", "Credit",
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
        "ONLINE ORDER|Type|Table|Customer|Tel|NOTES|NOTE|NO|+|Subtotal|Tax|Discount" +
        "|Customer discount|Promo|Delivery Fee|Tip|TOTAL|PAYMENT|CARD AT RESTAURANT|PAID|DUE|DELIVERY TO" +
        "|Instructions|Thank you for your visit!|Dine-in|Takeaway|Delivery|(No items in order)|Additional tip" +
        "|each|PAYMENT STATUS|UNPAID|PARTIALLY PAID|REFUNDED|OVERPAID|CREDIT");

    public static readonly PrintLabels German = Parse(
        "ONLINE-BESTELLUNG|Typ|Tisch|Kunde|Tel|NOTIZEN|NOTIZ|OHNE|+|Zwischensumme|MwSt|Rabatt" +
        "|Kundenrabatt|Promo|Liefergebühr|Trinkgeld|TOTAL|ZAHLUNG|KARTENZAHLUNG IM RESTAURANT|BEZAHLT|OFFEN|LIEFERUNG AN" +
        "|Hinweise|Vielen Dank für Ihren Besuch!|Im Lokal|Mitnehmen|Lieferung|(Keine Artikel in der Bestellung)|Zusätzliches Trinkgeld" +
        "|jeweils|ZAHLUNGSSTATUS|UNBEZAHLT|TEILWEISE BEZAHLT|ERSTATTET|ÜBERZAHLT|GUTHABEN");

    public static readonly PrintLabels French = Parse(
        "COMMANDE EN LIGNE|Type|Table|Client|Tél|REMARQUES|REMARQUE|SANS|+|Sous-total|TVA|Remise" +
        "|Remise client|Promo|Frais de livraison|Pourboire|TOTAL|PAIEMENT|CARTE AU RESTAURANT|PAYÉ|DÛ|LIVRAISON À" +
        "|Instructions|Merci de votre visite !|Sur place|À emporter|Livraison|(Aucun article dans la commande)|Pourboire supplémentaire" +
        "|chacun|ÉTAT DU PAIEMENT|NON PAYÉ|PARTIELLEMENT PAYÉ|REMBOURSÉ|TROP-PAYÉ|AVOIR");

    public static readonly PrintLabels Italian = Parse(
        "ORDINE ONLINE|Tipo|Tavolo|Cliente|Tel|NOTE|NOTA|SENZA|+|Subtotale|IVA|Sconto" +
        "|Sconto cliente|Promo|Costo di consegna|Mancia|TOTALE|PAGAMENTO|CARTA AL RISTORANTE|PAGATO|DA PAGARE|CONSEGNA A" +
        "|Istruzioni|Grazie per la visita!|Al tavolo|Da asporto|Consegna|(Nessun articolo nell'ordine)|Mancia aggiuntiva" +
        "|ciascuno|STATO DEL PAGAMENTO|NON PAGATO|PAGATO PARZIALMENTE|RIMBORSATO|PAGATO IN ECCESSO|CREDITO");

    public static readonly PrintLabels Spanish = Parse(
        "PEDIDO ONLINE|Tipo|Mesa|Cliente|Tel|NOTAS|NOTA|SIN|+|Subtotal|IVA|Descuento" +
        "|Descuento cliente|Promo|Gastos de envío|Propina|TOTAL|PAGO|TARJETA EN EL RESTAURANTE|PAGADO|PENDIENTE|ENTREGAR EN" +
        "|Indicaciones|¡Gracias por su visita!|En el local|Para llevar|Entrega|(Sin artículos en el pedido)|Propina adicional" +
        "|cada uno|ESTADO DEL PAGO|SIN PAGAR|PAGADO PARCIALMENTE|REEMBOLSADO|PAGADO DE MÁS|CRÉDITO");

    public static readonly PrintLabels Dutch = Parse(
        "ONLINE BESTELLING|Type|Tafel|Klant|Tel|OPMERKINGEN|LET OP|ZONDER|+|Subtotaal|BTW|Korting" +
        "|Klantkorting|Promo|Bezorgkosten|Fooi|TOTAAL|BETALING|KAARTBETALING IN HET RESTAURANT|BETAALD|OPENSTAAND|BEZORGEN AAN" +
        "|Instructies|Bedankt voor uw bezoek!|Ter plaatse|Meenemen|Bezorging|(Geen artikelen in de bestelling)|Extra fooi" +
        "|elk|BETAALSTATUS|ONBETAALD|GEDEELTELIJK BETAALD|TERUGBETAALD|TEVEEL BETAALD|TEGOED");

    public static readonly PrintLabels Turkish = Parse(
        "ONLINE SİPARİŞ|Tür|Masa|Müşteri|Tel|NOTLAR|NOT|YOK|+|Ara Toplam|KDV|İndirim" +
        "|Müşteri indirimi|Promosyon|Teslimat Ücreti|Bahşiş|TOPLAM|ÖDEME|RESTORANDA KARTLA ÖDEME|ÖDENEN|KALAN|TESLİMAT ADRESİ" +
        "|Talimatlar|Ziyaretiniz için teşekkürler!|Lokalda|Paket|Teslimat|(Siparişte ürün yok)|Ek bahşiş" +
        "|her biri|ÖDEME DURUMU|ÖDENMEDİ|KISMEN ÖDENDİ|İADE EDİLDİ|FAZLA ÖDENDİ|ALACAK");

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
            SelectedPrefix: fields[8],
            Subtotal: fields[9],
            Tax: fields[10],
            Discount: fields[11],
            CustomerDiscount: fields[12],
            Promo: fields[13],
            DeliveryFee: fields[14],
            Tip: fields[15],
            Total: fields[16],
            Payment: fields[17],
            CardAtRestaurant: fields[18],
            Paid: fields[19],
            Due: fields[20],
            DeliveryTo: fields[21],
            Instructions: fields[22],
            ThankYou: fields[23],
            DineIn: fields[24],
            TakeAway: fields[25],
            Delivery: fields[26],
            NoItems: fields[27],
            PaymentTip: fields[28],
            Each: fields[29],
            PaymentState: fields[30],
            Unpaid: fields[31],
            PartiallyPaid: fields[32],
            Refunded: fields[33],
            Overpaid: fields[34],
            Credit: fields[35]);
    }
}
