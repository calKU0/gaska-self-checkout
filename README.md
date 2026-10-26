# Self-Checkout Kiosk for Comarch ERP XL

A self-checkout kiosk built for a retail client: the customer scans or searches products, pays by card or BLIK on a
payment terminal and gets a fiscal receipt or a VAT invoice issued directly in the ERP. The application runs unattended on
a touch-screen workstation, talks to the ERP, a payment terminal and a fiscal printer, and recovers on its own from
failures in the middle of a payment.

The user interface is in Polish; code, comments and logs are in English.

## Highlights

* **ERP integration** – sales documents, customers and customer-specific prices are handled through the Comarch ERP XL
  native API (a 32-bit C library), with SQL for fast reads.
* **Card and BLIK payments** – own implementation of the Fiserv (Polcard) ECR-POS protocol over TCP: sale, DCC,
  reversal, terminal reports and recovery of the last transaction after a lost connection.
* **Fiscal printer** – own implementation of the POSNET protocol over a serial port: payment confirmations, reports,
  QR codes and bitmap images printed on non-fiscal forms.
* **Product search** – full-text search and category browsing in Elasticsearch, limited live to products in stock.
* **Invoices for businesses** – the customer enters a tax id; a missing customer record is created from public company
  registries (GUS REGON, VAT white list); invoices go to the national e-invoicing system (KSeF).
* **Reliability** – every step of a transaction is journaled; a paid transaction without a document is reversed
  automatically or finished from the service panel; a health monitor takes the kiosk out of service when a device fails
  and brings it back when it recovers.

## Technology

* **.NET 10**, **ASP.NET Core Blazor Server** – kiosk UI, service panel and workstation settings, hosted as a Windows service
  and shown in a browser in kiosk mode.
* **P/Invoke** to a 32-bit native ERP library on a dedicated STA thread with a priority queue (payments before
  background price calculation) – the whole solution targets x86.
* **Dapper** + **Microsoft.Data.SqlClient**, **Elasticsearch 7** over plain HTTP (`Utf8JsonWriter`, `JsonDocument`),
  **System.IO.Ports**, **System.Drawing.Common** (1-bit image conversion for the printer), **HttpClient** / SOAP.
* **Serilog** (application log and a separate frame-level protocol log for payment certification),
  **IOptionsMonitor** (settings changed in the UI apply without a restart), **System.Text.Json** (transaction journal).
* **xUnit** – protocol encoding/decoding, payment result mapping, cart rules, checkout scenarios, catalog and search.

## Architecture

Clean architecture – the domain and the use cases know nothing about the ERP or the devices; every integration sits
behind an interface in `Application`.

```
src/
  Gaska.SelfCheckout.Domain                 cart and its rules, product with stock per warehouse, category,
                                            customer, tax id, card transaction
  Gaska.SelfCheckout.Application            contracts (catalog, payment terminal, sales documents, customers, pricing,
                                            company registries, printer, journal), checkout orchestration, staff alerts,
                                            payment slip and pick-up ticket formatting
  Gaska.SelfCheckout.Infrastructure.Erp     ERP API session and thread, documents, customers, pricing,
                                            company registries, catalog (Elasticsearch + in-memory stock cache)
  Gaska.SelfCheckout.Devices.Polcard        ECR-POS protocol: TCP framing, TLV, transaction result mapping, reports
  Gaska.SelfCheckout.Devices.Posnet         POSNET protocol: CRC16, non-fiscal forms, QR codes, images
  Gaska.SelfCheckout.Infrastructure         transaction journal (JSON), settings store, image conversion
  Gaska.SelfCheckout.Web                    kiosk UI, service panel, settings, health monitor, hosting
tools/Gaska.SelfCheckout.DevTool            command-line harness for testing every integration on its own
tests/Gaska.SelfCheckout.Tests              unit tests
```

New devices (e.g. a scale) plug in the same way as the terminal and the printer: a `Devices.*` project implementing an
interface from `Application`.

## Purchase flow

1. The customer chooses a receipt or an invoice; for an invoice they enter a tax id, for a receipt they can optionally
   identify themselves with a customer card.
2. Products are added by scanning (a barcode reader working as a keyboard, recognized by typing speed), by text search or
   by browsing categories. Prices are calculated by the ERP for the identified customer and the quantity in the cart
   (quantity promotions are shown on product cards as price tiers).
3. Before payment the stock is checked again; the customer pays by card or BLIK.
4. While the customer pays, the ERP document is prepared; it is confirmed on approval and discarded when the payment is
   declined.
5. If the document cannot be completed the payment is reversed automatically; if the terminal stops answering, the result
   is recovered from the terminal by the transaction id.
6. A payment confirmation is printed on request, and a pick-up ticket (QR code, picking list, warehouse map) when part of
   the goods is collected from a back-office warehouse.

## Selected solutions

### Product search

Search and category browsing go to an Elasticsearch index; the index returns product ids only, while price and stock
always come from the ERP.

* By default only products in stock are shown: the ids of products in stock (kept in memory, refreshed every minute) are
  sent to the index as a filter, so searching and paging stay in Elasticsearch.
* Optionally all products are shown – those in stock first, the rest greyed out as temporarily unavailable. The cart
  itself refuses products without stock, so neither the product grid nor the scanner can add one.
* Category counts for both modes are computed in memory from product → category links read in the background with a
  parallel sliced scroll.
* Scanning never uses the index – a scanned code is checked directly against the ERP stock.
* When the index does not answer, the kiosk falls back to an in-memory search of the products in stock.

### Payment safety

Each transaction is a JSON file written atomically and updated at every step (payment started, approved, document
created, printed). The service panel lists transactions that need attention and offers checking the ERP document,
issuing it for an already paid cart (the transaction id prevents a duplicate) or reversing the payment.

### Health monitoring and staff alerts

The terminal, the fiscal printer, the ERP session and the database are checked periodically, never during a payment
(shared hardware lock). After repeated failures the kiosk shows an out-of-service screen and resumes by itself. Staff are
called with sounds played on the kiosk speaker – a help request from the customer or a device fault – and silence them
with a PIN.

### Payment terminal certification

The ECR-POS implementation is prepared for certification with the payment processor: every protocol frame is logged in
both directions, and the settings screen contains a built-in runner for the certification test cases with a log per test.
