# CMS-CSharp API

ASP.NET Core API for OMC CMS, backed by MySQL/MariaDB.

## Local Development

Start the Development environment with automatic full restarts:

```powershell
dotnet watch --no-hot-reload run --launch-profile development
```

Default URL:

```text
http://localhost:5089
```

Run with the local Production profile:

```powershell
dotnet run --launch-profile production
```

## API Endpoints

### API Status

```http
GET /
```

Returns the API name, running status, environment, and server time.

Success response: `200 OK`

```json
{
  "name": "OMC CMS API",
  "status": "running",
  "environment": "Development",
  "utcTime": "2026-08-25T00:00:00Z"
}
```

### Health Check

```http
GET /health
```

Success response: `200 OK`

```text
Healthy
```

### Database Connection Status

```http
GET /database/status
```

Opens a real database connection and executes a query. It does not only check whether a connection string exists.

Success response: `200 OK`

```json
{
  "connected": true,
  "provider": "MySql",
  "database": "OMC_Promotions_Dev",
  "server": "OPPONZ",
  "version": "10.3.32-MariaDB"
}
```

Failure response: `503 Service Unavailable`

### Database Configuration

`ConnectionStrings:DefaultConnection` is the single source for all MySQL connection settings, including server, port, database, user, password, and SSL options. MySqlConnector connection pooling is enabled by default, so opening and disposing connections per request reuses pooled connections efficiently.

The backend forces MySqlConnector `TreatTinyAsBoolean=False` for every connection. This ensures numeric `TINYINT(1)` status columns such as `Claims.status` preserve values such as `2`, `3`, and `4` instead of being converted to Boolean `true` and returned as `1`.

### Reusable Database Lookups

Reusable lookups for reference tables are defined by `Data/Repositories/IReferenceDataRepository.cs` and implemented in `Data/Repositories/ReferenceDataRepository.cs`. Feature services receive the repository through dependency injection and pass their current MySQL connection and transaction into it. This keeps SQL out of HTTP routes, avoids duplicate lookup code, and allows several writes and lookups to participate in the same transaction.

The repository currently provides:

- Channel lookup by unique `code`.
- Device-to-channel lookup by `model` and selected channel codes.
- Gift ID lookup by unique `alias`.

### Reusable Input Validation

Shared text normalization and validation rules are located in `Validation/CommonInputRules.cs`. Feature services can reuse the same methods for title-cased names and address text, normalized email addresses, digit-only contacts, four-digit postcodes, fixed-length numeric values, and required or optional printable ASCII text.

### Get Eligible Promotions by IMEI

```http
GET /api/promotions/eligible?imei={imei}
```

Finds a Device by its exact 15-digit IMEI and uses the Device `model` and `channel_code` to find applicable Promotions.

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `imei` | Yes | Exact 15-digit value from `Devices.imei`. |

Matching rules:

- `Promotion_Devices.eligible_model` must match the Device model.
- `Promotion_Channels.channel_code` must match the Device channel code.
- A matched `Promotion_Channels.end_date` must be on or after the New Zealand current calendar date minus three calendar months. Promotions that ended before that cutoff are excluded; ongoing and future Promotions remain eligible for this lookup.
- Matching Promotions are ordered by the matched Channel's `start_date` descending, then `end_date` descending, and then Promotion ID descending.
- The endpoint returns at most the three most recent matching Promotions. It may return one or two when fewer matches exist, and an empty array when none exist.
- Device category and redemption status are not used as filters.
- `Promotions.banner_url` is treated as the stored banner filename and expanded to `{R2_PUBLIC_ASSETS_URL}/banners/Promotions/{banner-file}`. An already absolute banner URL is returned unchanged.
- The response returns the public image URL; it does not proxy the image binary through this API.
- Device model, channel code, Promotion description, and Gifts are used or resolved internally as needed but are not included in the response. The queried IMEI is included in the response.
- Device matching, the three most recent Promotions within the three-month window, and Claim records are read in one database command to reduce request latency.
- `claimIds` contains an object with `Claims.id` as `id` and `Claims.status` as `status` for every Claim belonging to the queried IMEI. Items are ordered by `created_at` and then ID descending. It is an empty array when the IMEI has no Claim, so its length always equals the number of matching Claims.
- Each returned Promotion includes the matched `Channels.name` as `channelName` and the corresponding `Promotion_Channels.start_date`, `end_date`, and `redeem_end_date`, formatted as `yyyy-MM-dd HH:mm:ss`. The response fields are `startDate`, `endDate`, and `redeemEndDate`; the channel code is not returned.

Success response: `200 OK`

```json
{
  "imei": "490154203237518",
  "claimIds": [
    {
      "id": "OPNZPROCLM-260828-4EUZB66Y",
      "status": 1
    },
    {
      "id": "OPNZPROCLM-260827-8KM80SDG",
      "status": 0
    }
  ],
  "promotions": [
    {
      "id": 123,
      "name": "Example Promotion",
      "bannerUrl": "https://assets.example.com/banners/Promotions/banner-uuid.webp",
      "channelName": "Spark",
      "startDate": "2026-09-01 00:00:00",
      "endDate": "2026-09-28 23:59:59",
      "redeemEndDate": "2026-10-12 23:59:59"
    }
  ]
}
```

An existing Device with no applicable Promotions returns `200 OK` with an empty `promotions` array.

Invalid or missing IMEI response: `400 Bad Request`

Unknown IMEI response: `404 Not Found`

Database or configuration failure response: `503 Service Unavailable`

### List Promotions

```http
GET /api/promotions
```

Returns the latest 15 normal Promotions for display without query parameters. The reserved General Case Promotion (`Promotions.id = 0`) is excluded. "Latest" means the highest `Promotions.id` values, ordered descending. No date window is applied.

Data and aggregation rules:

- `promotionId`, `name`, and the stored Banner filename come from `Promotions`.
- `bannerUrl` is a complete clickable URL under `{R2_PUBLIC_ASSETS_URL}/banners/Promotions/{banner-file}`. An already absolute database URL is returned unchanged; the API does not proxy the image binary.
- `channelCategories` contains distinct `Channels.category` values joined through `Promotion_Channels.channel_code = Channels.code`. Ten Channels with the same Category therefore return that Category only once.
- `marketNames` contains distinct `Devices.market_name` values joined through `Promotion_Devices.eligible_model = Devices.model`. Empty values and `temp` are excluded after trimming and case-insensitive comparison.
- `gifts` contains objects joined through `Promotion_Gifts` and `Gifts`. Each object's `name` is the display value `Gifts.name + " " + Gifts.color`; when color is empty or equals `Empty` case-insensitively, it contains only `Gifts.name`. The object's `alias` comes from the unique `Gifts.alias` and can be sent back by an edit form.
- Related values are returned as arrays and are empty when the Promotion has no matching rows. All relationship queries are limited to the same latest 15 Promotion IDs, and the separate queries avoid multiplication caused by joining several many-to-many relationships together.

Success response: `200 OK`

```json
[
  {
    "promotionId": 123,
    "name": "Example Promotion",
    "bannerUrl": "https://assets.example.com/banners/Promotions/banner-uuid.webp",
    "channelCategories": ["Carrier", "Retailer"],
    "marketNames": ["Find X8 Pro", "Reno14"],
    "gifts": [
      {
        "name": "OPPO Watch X2 Black",
        "alias": "WATCH X2 BLACK"
      },
      {
        "name": "Travel Bag",
        "alias": "TRAVEL BAG"
      }
    ]
  }
]
```

No matching Promotions response: `200 OK` with `[]`

Database, R2 public URL configuration, or connection failure response: `503 Service Unavailable`

### Get Promotion Details

```http
GET /api/promotions/{promotionId}
```

Returns one Promotion and the related Channel periods and eligible Device models for the exact numeric Promotion ID. No date window is applied.

Response rules:

- `promotionId`, `description`, `slugUrl`, and `termsUrl` come from `Promotions.id`, `description`, `slug_url`, and `terms_url`.
- `slugUrl` is returned exactly as stored and does not receive a `/promotions/` prefix.
- `termsUrl` is returned exactly as stored. It may be `/terms` or the complete public R2 URL of an uploaded Terms file.
- `channels` joins `Promotion_Channels.channel_code` to `Channels.code` and returns `Channels.name`, `Promotion_Channels.start_date`, and `end_date`. `redeem_end_date` and channel code are not returned.
- `devices` joins `Promotion_Devices.eligible_model` to `Devices.model` and returns distinct `Devices.market_name` and `Promotion_Devices.eligible_model` as `marketName` and `model`.
- Device market names that are empty or equal `temp` after trimming and case-insensitive comparison are excluded.
- Dates use `yyyy-MM-dd HH:mm:ss`. Related collections are empty arrays when no rows match.

Success response: `200 OK`

```json
{
  "promotionId": 123,
  "description": "Example promotion description",
  "slugUrl": "example-promotion-a1b2c3d4",
  "termsUrl": "/terms",
  "channels": [
    {
      "name": "Example Retailer",
      "startDate": "2026-09-01 00:00:00",
      "endDate": "2026-09-30 23:59:59"
    }
  ],
  "devices": [
    {
      "marketName": "Find X8 Pro",
      "model": "CPH2659"
    }
  ]
}
```

Negative Promotion ID response: `400 Bad Request`

Unknown Promotion response: `404 Not Found`

Database, configuration, or connection failure response: `503 Service Unavailable`

### Create Promotion

```http
POST /api/promotions
Content-Type: multipart/form-data
```

Creates a promotion, uploads its files to Cloudflare R2, and inserts all related database records in one MySQL transaction.

Channel, Device, Gift, and duplicate-conflict lookups are batched. When both a banner and Terms file are supplied, their R2 uploads run concurrently.

Multipart form fields:

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `name` | Text | Yes | Promotion name. |
| `description` | Text | Yes | Promotion description. |
| `products` | JSON array text | Yes | Devices selected by `model`. |
| `channels` | JSON array text | Yes | Channels selected by unique `code`; `startDate` and `endDate` use `yyyy-MM-dd` without a time component. |
| `gifts` | JSON array text | Yes | Existing Gifts selected by unique `alias`. |
| `banner` | File | Yes | Non-empty image file. |
| `terms` | Text or file | Yes | Either the exact text `/terms` or one uploaded terms file, but not both. |

Example JSON value for `products`:

```json
[{"model":"CPH2831"}]
```

Example JSON value for `channels`:

```json
[
  {
    "code": "SPK",
    "startDate": "2026-09-01",
    "endDate": "2026-09-28"
  }
]
```

Example JSON value for `gifts`:

```json
[{"alias":"ENCO BUDS3PRO WHITE"}]
```

Storage and database behavior:

- The banner is renamed to a UUID while retaining a safe extension.
- The banner is uploaded to `banners/Promotions/{uuid}.{extension}`.
- `Promotions.banner_url` stores the renamed banner file name, not the full URL.
- A terms file is renamed to a UUID and uploaded to `terms/Promotions/{uuid}.{extension}`.
- For a terms file, `Promotions.terms_url` stores its public R2 URL.
- For text terms, `Promotions.terms_url` stores `/terms`.
- `Promotions.slug_url` is generated from the promotion name as `{name-slug}-{unique-suffix}` and checked against existing promotion slugs before insertion. The stored value does not include a `/promotions/` prefix.
- The backend stores `startDate` as `start_date` at `00:00:00` and `endDate` as `end_date` at `23:59:59`.
- `redeem_end_date` is exactly 14 calendar days after `end_date` and retains `23:59:59` (for example, an `endDate` of `2026-08-31` produces `2026-09-14 23:59:59`).
- The backend resolves the effective models, channels, channel periods, and gift IDs before inserting the promotion. It runs the reusable conflict detector against existing promotions, then inserts `Promotions` first and uses its generated ID for the related rows.
- A channel must exist by its unique `code`.
- A product is matched only by its unique `model`. The Devices table must contain that model with at least one submitted, valid channel code. Otherwise the model is skipped and is not inserted into `Promotion_Devices.eligible_model`.
- A valid submitted channel is inserted into `Promotion_Channels` only when at least one accepted device model is associated with that channel in the Devices table.
- Gifts are matched only by unique `alias`; an unknown alias rejects the request with `400 Bad Request`.
- A conflict exists when an existing promotion has exactly the same effective model set, channel-code set, and gift-ID set, and at least one matching channel period overlaps inclusively. Periods overlap when `newStart <= existingEnd` and `newEnd >= existingStart`. An end date followed by the next calendar day's start date is allowed; using the same calendar date is an overlap.
- Valid related rows are inserted into `Promotion_Devices`, `Promotion_Channels`, and `Promotion_Gifts`.
- If database work fails, the transaction is rolled back and files uploaded by the request are removed from R2.

Success response: `201 Created`

```json
{
  "id": 123,
  "name": "OPPO Buds3 Pro for A6 5G Spark Only",
  "slugUrl": "oppo-buds3-pro-for-a6-5g-spark-only-a1b2c3d4",
  "termsUrl": "/terms",
  "bannerFileName": "e42f7f58dd3f4de1b06573bd7b8dbb20.webp",
  "productCount": 1,
  "skippedProductCount": 0,
  "channelCount": 1,
  "skippedChannelCount": 0,
  "giftCount": 1
}
```

The success counts report inserted and skipped products/channels. A promotion can be created with zero related device or channel rows if none of the submitted products or channels match their source tables.

Validation failure response: `400 Bad Request`

Duplicate or overlapping promotion response: `409 Conflict`

```json
{
  "error": "A promotion with the same models, channels, and gifts has an overlapping channel period.",
  "existingPromotion": {
    "id": 122,
    "name": "Existing promotion",
    "slugUrl": "existing-promotion-a1b2c3d4"
  },
  "overlappingChannelCodes": ["SPK"]
}
```

Non-multipart request response: `415 Unsupported Media Type`

Database or R2 failure response: `503 Service Unavailable`

### Get Claims

```http
GET /api/claims
```

Returns the union of (1) Claims created from the start of the previous New Zealand calendar week through the current request time and (2) all Claims whose current `Claims.status` equals `0`, regardless of creation date. For example, a request made on Wednesday returns the complete previous Monday-to-Sunday period, the current Monday through Wednesday up to the request time, and any older unresolved status-0 Claims. A Claim matching both rules is returned only once. Results are ordered by `Claims.created_at` descending and then Claim ID descending.

Rules:

- `claimId` comes from `Claims.id`.
- `imei` comes from `Claims.imei`.
- `fullName` combines `Customers.first_name` and `Customers.last_name` with one space.
- `email` comes from `Customers.email`.
- `status` and `createdAt` come from `Claims.status` and `Claims.created_at`.
- `gifts` contains distinct values from Gifts linked through `Claim_Gifts`. Each value is `Gifts.name + space + Gifts.color`; when `color` is empty or equals `Empty` (case-insensitive), only `Gifts.name` is returned. Multiple Gifts do not duplicate the Claim row.
- `createdAt` is formatted as `yyyy-MM-dd HH:mm:ss`. Historical rows whose `Claims.created_at` is database `NULL` return `createdAt: null` instead of failing the request.
- Week boundaries are calculated in the `Pacific/Auckland` time zone and converted to UTC for comparison with `Claims.created_at`. The date boundary does not apply to status-0 Claims.
- The endpoint has no pagination input or 50-row limit; it returns every Claim in this date range.

Success response: `200 OK`

```json
[
  {
    "claimId": "OPNZPROCLM-260903-4EUZB66Y",
    "imei": "490154203237518",
    "fullName": "Chris Example",
    "email": "customer@example.com",
    "status": 0,
    "gifts": [
      "OPPO Gift Black"
    ],
    "createdAt": "2026-09-03 10:30:00"
  }
]
```

When no Claims exist, the endpoint returns `200 OK` with an empty array.

Database or configuration failure response: `503 Service Unavailable`

### Search Claims by Claim ID

```http
GET /api/claims/search?claim_id={claimId}
```

Performs a case-insensitive contains search against `Claims.id` without applying the previous-week/current-week date restriction used by `GET /api/claims`.

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `claim_id` | Yes | Full or partial Claim ID. `%`, `_`, and the escape character are treated as literal search text. |

The response shape, Gift formatting, deduplication, and ordering are the same as `GET /api/claims`. Results are ordered by `Claims.created_at` descending and then Claim ID descending.

Success response: `200 OK`

```json
[
  {
    "claimId": "OPNZPROCLM-260831-KICYCCH1",
    "imei": "868874080676538",
    "fullName": "Chris Example",
    "email": "customer@example.com",
    "status": 2,
    "gifts": [
      "OPPO Gift Black"
    ],
    "createdAt": "2026-08-30 23:32:57"
  }
]
```

No matches response: `200 OK` with an empty array.

Missing or empty `claim_id` response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Search Claims by IMEI

```http
GET /api/claims/search/imei?imei={imei}
```

Performs a contains search against `Claims.imei` without a date restriction. The response shape, Gift formatting, deduplication, and ordering are the same as `GET /api/claims`.

The required `imei` parameter may contain a full or partial IMEI. `%`, `_`, and the escape character are treated as literal search text.

Success response: `200 OK`; no matches return an empty array.

Missing or empty `imei` response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Search Claims by Customer Email

```http
GET /api/claims/search/email?email={email}
```

Performs a contains search against `Customers.email`, joined through `Claims.customer_id = Customers.id`, without a date restriction. The response shape, Gift formatting, deduplication, and ordering are the same as `GET /api/claims`.

The required `email` parameter may contain a full or partial customer email. `%`, `_`, and the escape character are treated as literal search text.

Success response: `200 OK`; no matches return an empty array.

Missing or empty `email` response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Search Claims by Delivery Reference

```http
GET /api/claims/search/reference?reference={reference}
```

Performs a contains search against `Deliveries.reference`, matched through `Deliveries.claim_id = Claims.id`, without a date or Claim-status restriction. Any matching Delivery reference qualifies its Claim; multiple matching Delivery rows do not duplicate the Claim. The required `reference` parameter accepts full or partial text, with `%`, `_`, and the escape character treated literally.

The response is the same Claim array as the Claim ID, IMEI, and Email searches: `claimId`, `imei`, `fullName`, `email`, `status`, `gifts`, and `createdAt`. Gift display names are deduplicated, and results are ordered by `Claims.created_at` descending and Claim ID descending. A database `NULL` creation date returns `createdAt: null`.

Success response: `200 OK`; no matches return `[]`.

Missing or empty `reference` response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Get Claim Details

```http
GET /api/claims/view/{claimId}
```

Returns exactly one Claim matched by `Claims.id`.

Response rules:

- `claimId` comes from `Claims.id`.
- `email` and `contact` come from the related `Customers` row.
- `street`, `suburb`, `city`, `postcode`, and `instructions` come from the latest current `Deliver_Addresses` row. Missing or database `NULL` values are returned as empty strings. `fullAddress` is also retained as the combined display value.
- `giftAliases` contains every related `Gifts.alias` found through `Claim_Gifts`; no Gift returns an empty array.
- `receiptUrl` and `imeiCopyUrl` use the filenames from `Claims.receipt_url` and `Claims.screenshot_url`. The backend obtains `Claims.promotion_id` and searches Cloudflare R2 with the prefix `claims/promotions/{promotionId}/{partial-file-name}`. A unique match is returned with its complete UUID filename and extension. Both lookups run concurrently.
- `receiptSha256` and `imeiCopySha256` are lowercase SHA-256 hashes calculated from the resolved R2 object contents. They are `null` for legacy absolute URLs, unresolved or ambiguous object prefixes, or an R2 read failure.
- If an older database value contains folders, only its final filename is used for the new Promotion-ID folder lookup. Legacy absolute URLs are returned unchanged. If a prefix finds no object, finds multiple objects, or the R2 lookup fails, the API uses the unresolved Promotion-ID path and logs a warning instead of selecting an uncertain file.
- `Claims.status` is used internally but is not returned. When it equals `1`, `reference` contains the latest related `Deliveries.reference` and `trackLink` is `null`.
- When the internal status equals `2`, `reference` contains the latest related `Deliveries.reference` and `trackLink` contains the latest `Track_Trace.track_link` for the current delivery address.
- For all other internal statuses, `reference` and `trackLink` are `null`.
- The API returns public file URLs, not image binary data.

Success response: `200 OK`

```json
{
  "claimId": "OPNZPROCLM-260903-4EUZB66Y",
  "promotionName": "Example Promotion",
  "email": "customer@example.com",
  "contact": "0211234567",
  "street": "1 Example Street",
  "suburb": "Newmarket",
  "city": "Auckland",
  "postcode": "1023",
  "instructions": "Leave at reception",
  "fullAddress": "1 Example Street, Newmarket, Auckland, 1023",
  "giftAliases": ["ENCO BUDS3PRO WHITE"],
  "receiptUrl": "https://assets.example.com/claims/promotions/123/receipt-uuid.jpg",
  "receiptSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "imeiCopyUrl": "https://assets.example.com/claims/promotions/123/imei-copy-uuid.png",
  "imeiCopySha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "reference": "DELIVERY-REFERENCE-123",
  "trackLink": "https://tracking.example.com/example-reference"
}
```

Unknown Claim response: `404 Not Found`

Invalid Claim ID response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Get Claim Fulfilment Rows

```http
GET /api/claims/fulfilment/{claimId}
```

For one or more Claim IDs in a single request, use:

```http
GET /api/claims/fulfilment?claimIds={claimId1}&claimIds={claimId2}
```

The `claimIds` parameter may be supplied once or repeated, and comma-separated IDs are also accepted. Duplicate IDs are removed case-insensitively, and at most 50 unique IDs may be requested. The original path route remains available for one exact `Claims.id`.

The endpoint joins `Claims`, `Customers`, `Claim_Gifts`, `Gifts`, and `Deliver_Addresses`. The response is always one flat array with one row per related Claim Gift/SKU. Therefore, two requested Claims with two Gifts each may return four rows. Claim, customer, and delivery values repeat for each Gift. Unknown IDs and Claims without a related Gift are omitted from the batch response; if none match, it returns `200 OK` with `[]`.

The latest delivery address is selected by preferring `is_current = 1` and then the highest `Deliver_Addresses.id`. Missing nullable address values and instructions are returned as empty strings.

Response field rules:

- Direct database values retain their database column names except that `Claims.id` is returned as `claimId`. The remaining direct names are `alias`, `street`, `suburb`, `city`, `postcode`, `contact`, `email`, and `instructions`. IMEI and Promotion ID are not returned.
- `clientOrderNumber`, `consigneePoNumber`, and `shippingAddressLine3` are generated empty strings.
- `lineId` is the generated constant `GWP`; `qty` is the generated constant integer `1`.
- `fullName` is generated by combining `Customers.first_name` and `last_name`.
- `alias` comes from `Gifts.alias` through `Claim_Gifts`.

Success response: `200 OK`

```json
[
  {
    "claimId": "OPNZPROCLM-260903-QZ9RHCVK",
    "clientOrderNumber": "",
    "consigneePoNumber": "",
    "lineId": "GWP",
    "alias": "WATCH X2 BLACK",
    "qty": 1,
    "fullName": "Example Customer",
    "street": "1 Example Street",
    "suburb": "Newmarket",
    "shippingAddressLine3": "",
    "city": "Auckland",
    "postcode": "1023",
    "contact": "0211234567",
    "email": "customer@example.com",
    "instructions": "Leave at reception"
  }
]
```

The single-ID path returns `404 Not Found` for an unknown Claim or a Claim without a related Gift.

Missing IDs, empty IDs, or more than 50 unique IDs response: `400 Bad Request`

Database or configuration failure response: `503 Service Unavailable`

### Update Claim

```http
PATCH /api/claims/{claimId}
Content-Type: multipart/form-data
```

Partially updates one Claim and its related Customer, current delivery address, Gifts, and R2 assets. Only submitted fields are changed.

Multipart form fields:

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `email` | Text | No | Replaces the related `Customers.email`; normalized and validated like Claim creation. |
| `contact` | Text | No | Replaces `Customers.contact`; whitespace is removed and the result must contain digits only. |
| `street` | Text | No | Replaces the current delivery address street. |
| `suburb` | Text | No | Replaces the current delivery address suburb. |
| `city` | Text | No | Replaces the current delivery address city. |
| `postcode` | Text | No | Replaces the current delivery postcode and must contain exactly four digits. |
| `instructions` | Text | No | Replaces delivery instructions. An explicitly submitted empty value is stored as `""`; omission leaves the existing value unchanged. |
| `giftAliases` | JSON array text | No | Replaces all existing `Claim_Gifts`. When supplied, the array must contain at least one unique Gift alias. |
| `receipt` | File | No | Replaces the existing Receipt with a UUID-named JPG, JPEG, PNG, or PDF file up to 5 MB. |
| `imeiCopy` | File | No | Replaces the existing IMEI-copy image stored through `Claims.screenshot_url`; accepts JPG, JPEG, PNG, or PDF up to 5 MB. |

At least one field or replacement file must be supplied. Gift aliases are resolved to real `Gifts.id` values. For Claims with `promotion_id > 0`, each Gift must belong to that Promotion; for the General Case with `promotion_id = 0`, Promotion-Gift membership is not checked.

Replacement files are uploaded to `claims/promotions/{promotionId}/{uuid}.{extension}`. The database continues to store only the UUID filename and extension. Database changes are transactional. A newly uploaded file is removed if the database update fails; after a successful commit, the old corresponding R2 file is deleted. Omitting a file keeps the existing file unchanged.

The endpoint does not change the Claim ID, Promotion ID, IMEI, purchase date, Claim status, email status, customer name, or Device row.

Success response: `200 OK`

```json
{
  "success": true,
  "claimId": "OPNZPROCLM-260903-4EUZB66Y"
}
```

Invalid fields, files, Gifts, or an empty update response: `400 Bad Request`

Unknown Claim response: `404 Not Found`

Wrong content type response: `415 Unsupported Media Type`

Database, configuration, or R2 failure response: `503 Service Unavailable`

### Delete Claim

```http
DELETE /api/claims/{claimId}
```

Deletes one Claim and its directly related records in a single MySQL transaction.

Deletion order and rules:

1. Lock the matching `Claims` row and read its `customer_id`.
2. Delete all matching `Claim_Gifts` rows by `claim_id`.
3. Delete all matching `Deliver_Addresses` rows by `claim_id`.
4. Delete the matching `Claims` row by ID.
5. Delete the corresponding `Customers` row only when no other Claim still references that Customer.

The endpoint does not delete or update the matching `Devices` row or IMEI. If any database deletion fails, the transaction rolls back all database deletions. After the database transaction commits, Receipt and Screenshot are deleted concurrently from Cloudflare R2 using `claims/promotions/{promotionId}/{stored-file-name}` prefix matching.

MySQL and Cloudflare R2 cannot participate in one shared transaction. If R2 cleanup fails or a stored partial filename does not resolve uniquely, the committed database deletion remains successful, the failed object is not guessed or deleted, and the response reports the cleanup result.

Success response: `200 OK`

```json
{
  "success": true,
  "claimId": "OPNZPROCLM-260903-4EUZB66Y",
  "assetsCleanupSucceeded": true
}
```

Database row counts and the R2 deletion count are written to the backend log and are not exposed in the API response. `success` means the database deletion committed. `assetsCleanupSucceeded` is `true` only when both R2 files were deleted.

Unknown Claim response: `404 Not Found`

Invalid Claim ID response: `400 Bad Request`

Database constraint or connection failure response: `503 Service Unavailable`

### Create Claim

```http
POST /api/claims
Content-Type: multipart/form-data
```

Creates a customer and claim, records its gifts and delivery address, uploads the receipt and screenshot to Cloudflare R2, marks the Device as redeemed, and queues the claim confirmation email for background delivery.

The receipt and screenshot are uploaded concurrently. Reference checks are batched before the short database write transaction to reduce request latency and lock time.

Multipart form fields:

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `promotionId` | Integer | Yes | ID of the selected existing Promotion. `0` is the reserved General Case Promotion ID. Negative values are rejected. |
| `imei` | Text | Yes | An exact 15-digit Device IMEI. |
| `purchaseDate` | Text | Yes | Purchase date in `yyyy-MM-dd` format. |
| `firstName` | Text | Yes | Customer first name. |
| `lastName` | Text | Yes | Customer last name. |
| `email` | Text | Yes | Customer email address. |
| `contact` | Text | Yes | Customer contact number. |
| `street` | Text | Yes | Delivery street. |
| `suburb` | Text | Yes | Delivery suburb. |
| `city` | Text | Yes | Delivery city. |
| `postcode` | Text | Yes | Delivery postcode. |
| `instructions` | Text | No | Optional delivery instructions. If omitted, empty, or whitespace-only, the backend stores an empty string (`""`), not `NULL`. |
| `giftAliases` | JSON array text | Yes | One or more unique Gift aliases selected from the Promotion. |
| `receipt` | File | Yes | JPG, JPEG, PNG, or PDF file up to 5 MB. |
| `screenshot` | File | Yes | JPG, JPEG, PNG, or PDF file up to 5 MB. |

Example `giftAliases` value:

```json
["ENCO BUDS3PRO WHITE"]
```

Validation and persistence rules:

- All submitted text is trimmed before validation and storage, so stored values do not end with spaces.
- Text fields accept printable ASCII English letters, digits, spaces, and symbols only.
- `firstName`, `lastName`, `street`, `suburb`, and `city` are normalized to title case for every space-separated word; repeated spaces are collapsed.
- `email` is trimmed, converted to lowercase, and validated as an email address.
- All whitespace is removed from `contact`, which must then contain digits only.
- `postcode` must contain exactly four digits; a leading zero is retained.
- The Promotion must exist, including the reserved `Promotions.id = 0` row when `promotionId` is `0`.
- The IMEI must contain exactly 15 digits and exist in `Devices`. Its existing `redemption_status` value is not used to reject the Claim.
- Device model, channel, and `purchaseDate` are not used to determine Claim eligibility. `purchaseDate` is validated only as `yyyy-MM-dd` and stored in `Claims.purchase_date`.
- Every Gift alias is resolved to a real `Gifts.id`. For a normal Promotion (`promotionId > 0`), each Gift must also exist in `Promotion_Gifts` for that Promotion. For the General Case (`promotionId = 0`), the `Promotion_Gifts` membership check is skipped, so any existing Gift may be recorded in `Claim_Gifts`.
- A new `Customers` row is inserted first and its generated ID is stored in `Claims.customer_id`.
- The Claim ID format is `OPNZPROCLM-yyMMdd-XXXXXXXX`, using the current `Pacific/Auckland` date. The final eight characters are cryptographically generated uppercase letters or digits, and the generated ID is checked against `Claims.id` before use.
- Receipt and screenshot files are independently renamed to UUID filenames while retaining their validated extensions. The backend validates both the extension and the file signature, rejects files larger than 5 MB, and uploads both files concurrently.
- Both files are uploaded under `claims/promotions/{promotionId}/{uuid}.{extension}`.
- `Claims.receipt_url` and `Claims.screenshot_url` store only `{uuid}.{extension}`. The public domain, `claims/promotions/` prefix, and Promotion ID folder are not stored in these columns.
- `Claims.status` and `Claims.email_status` initially use `0`.
- Selected Gifts are inserted into `Claim_Gifts`, and the delivery address is inserted into the existing `Deliver_Addresses` table with `is_current = 1`.
- Before the Claim transaction commits, the matching Device is always updated to `redemption_status = 1`, regardless of its previous value.
- If any database operation fails, the transaction is rolled back and files uploaded by the request are removed from R2.
- After the Claim transaction commits, the backend queues the confirmation email and returns without waiting for SMTP. A background service sends the email to `Customers.email`; the message contains the customer name, Claim ID, selected Gift names and colors, and delivery address.
- A successful email changes `Claims.email_status` to `1`. A failed email leaves it at `0`, logs the failure, and attempts to notify `EMAIL_ADMIN`.
- SMTP failure does not roll back an already committed Claim and does not make the frontend retry the full Claim creation request.

Success response: `201 Created`

```json
{
  "id": "OPNZPROCLM-260827-4EUZB66Y",
  "promotionId": 123,
  "customerId": 456,
  "imei": "490154203237518",
  "giftIds": [12],
  "receiptUrl": "uuid.pdf",
  "screenshotUrl": "uuid.png",
  "emailQueued": true
}
```

`emailQueued` reports whether the committed Claim was accepted by the in-process email queue; it does not mean SMTP delivery has already completed. `Claims.email_status` remains `0` while queued or after a failed send and changes to `1` only after successful delivery.

Validation failure response: `400 Bad Request`

Non-multipart request response: `415 Unsupported Media Type`

Database or R2 failure response: `503 Service Unavailable`

### Search Device Models

```http
GET /api/devices/search?market_name={market_name}
```

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `market_name` | Yes | Performs a contains search against `Devices.market_name`. |

Additional filters:

- `category` must be exactly `11`.
- `redemption_status` must be `0`.
- The related `Channels.category` must be `Retailer` or `Carrier` (case-insensitive).
- No Channel codes are excluded individually.
- Devices are joined to `Channels` by `Devices.channel_code = Channels.code`.
- Duplicate Device results are merged by `market_name + model`.
- Each Device result contains all distinct matching Channel names and codes.

Example request:

```http
GET /api/devices/search?market_name=Tem
```

Success response: `200 OK`

```json
[
  {
    "market_name": "Temp",
    "model": "CPH2689",
    "channels": [
      {
        "channel_name": "Example Retailer",
        "channel_code": "EX01"
      }
    ]
  }
]
```

Missing or empty parameter response: `400 Bad Request`

Database failure response: `503 Service Unavailable`

### Search Gifts

```http
GET /api/gifts/search?name={name}
```

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `name` | Yes | Performs a contains search against `Gifts.name`. |

Identical results are merged by `name + alias + color + status`.

Gift status values:

- `0`: Available and active.
- `1`: No longer offered and should not be selected for new claims.
- `2`: Temporarily out of stock.

Example request:

```http
GET /api/gifts/search?name=Watch
```

Success response: `200 OK`

```json
[
  {
    "name": "OPPO Watch",
    "alias": "Watch",
    "color": "Black",
    "status": 0
  }
]
```

Missing or empty parameter response: `400 Bad Request`

Database failure response: `503 Service Unavailable`

### Get Channels

```http
GET /api/channels
```

Returns all channels ordered by `code`. This endpoint does not accept search parameters.

Success response: `200 OK`

```json
[
  {
    "code": "HVNM",
    "name": "Harvey Norman",
    "category": "Retailer"
  }
]
```

Database failure response: `503 Service Unavailable`

### Search Channels

```http
GET /api/channels/search?name={name}
```

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `name` | Yes | Performs a contains search against `Channels.name`. |

Identical results are merged by `name + code + category`.

Example request:

```http
GET /api/channels/search?name=Harvey
```

Success response: `200 OK`

```json
[
  {
    "name": "Harvey Norman",
    "code": "HVNM",
    "category": "Retailer"
  }
]
```

Missing or empty parameter response: `400 Bad Request`

Database failure response: `503 Service Unavailable`

## Development CORS

The Development environment allows credentials from these frontend origins:

```text
http://localhost:3000
```

The allowed origins are configured in `appsettings.Development.json`.

## Email Configuration

Development and Production configuration files are ignored by Git and may contain local or server-specific values. Real deployment credentials should preferably be supplied through process environment variables or a secrets provider instead of being committed to Git.

| Key | Purpose |
| --- | --- |
| `EMAIL_HOST` | SMTP server hostname. |
| `EMAIL_PORT` | SMTP server port. |
| `EMAIL_USER` | SMTP authentication username and customer-service contact shown in Claim confirmation emails. |
| `EMAIL_PASS` | SMTP authentication password. |
| `EMAIL_FROM` | Sender address used in the email `From` header. |
| `EMAIL_ADMIN` | Administrative recipient for Claim email failure alerts. |

PowerShell environment-variable example:

```powershell
$env:EMAIL_HOST="smtp.example.com"
$env:EMAIL_PORT="587"
$env:EMAIL_USER="smtp-user"
$env:EMAIL_PASS="smtp-password"
$env:EMAIL_FROM="no-reply@example.com"
$env:EMAIL_ADMIN="admin@example.com"
```

## Development Cloudflare R2 Configuration

The Development environment defines these Cloudflare R2 configuration placeholders in `appsettings.Development.json`:

| Key | Description |
| --- | --- |
| `R2_PUBLIC_ASSETS_URL` | Public base URL used to access uploaded assets. |
| `R2_ENDPOINT` | Cloudflare R2 S3-compatible endpoint. |
| `R2_BUCKET` | R2 bucket name. |
| `R2_ACCESS_KEY_ID` | R2 access key ID. |
| `R2_SECRET_ACCESS_KEY` | R2 secret access key. |
| `R2_UPLOAD_MAX_BYTES` | Maximum allowed upload size in bytes. |

Keep secret values out of committed configuration. Set real local values through environment variables when possible. PowerShell example:

```powershell
$env:R2_PUBLIC_ASSETS_URL="https://assets.example.com"
$env:R2_ENDPOINT="https://account-id.r2.cloudflarestorage.com"
$env:R2_BUCKET="bucket-name"
$env:R2_ACCESS_KEY_ID="access-key-id"
$env:R2_SECRET_ACCESS_KEY="secret-access-key"
$env:R2_UPLOAD_MAX_BYTES="10485760"
```

### Internal R2 Upload Service

`IR2StorageService` is an internal service registered as a singleton dependency. It can only be injected into application services inside this project and is not exposed as a general-purpose HTTP upload endpoint. It validates the R2 configuration, rejects files larger than `R2_UPLOAD_MAX_BYTES`, uploads through the R2 S3-compatible API, supports cleanup deletion, and returns the object key, public URL, ETag, and uploaded size. Upload requests disable the AWS SDK streaming SigV4 payload/trailer format and default SDK checksum trailer because Cloudflare R2 does not support that upload format.

Example usage:

```csharp
internal sealed class AssetService(IR2StorageService storage)
{
    internal Task<R2UploadResult> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        var objectKey = $"assets/{Guid.NewGuid():N}-{fileName}";

        return storage.UploadAsync(
            content,
            objectKey,
            contentType,
            cancellationToken);
    }
}
```

The upload service does not close streams supplied by the caller. No public upload API endpoint is currently exposed.

## API Documentation Maintenance Rule

Every API addition, modification, rename, or deletion must update this README in the same change. Keep the endpoint list, request parameters, filtering rules, response formats, examples, and HTTP status codes synchronized with the implementation.
