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

### Reusable Database Lookups

Reusable lookups for reference tables are defined by `Data/Repositories/IReferenceDataRepository.cs` and implemented in `Data/Repositories/ReferenceDataRepository.cs`. Feature services receive the repository through dependency injection and pass their current MySQL connection and transaction into it. This keeps SQL out of HTTP routes, avoids duplicate lookup code, and allows several writes and lookups to participate in the same transaction.

The repository currently provides:

- Channel lookup by unique `code`.
- Device-to-channel lookup by `model` and selected channel codes.
- Gift ID lookup by unique `alias`.

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
- Channel `start_date`, `end_date`, and `redeem_end_date` are intentionally not checked by this endpoint.
- Device category and redemption status are not used as filters.
- `Promotions.banner_url` is treated as the stored banner filename and expanded to `{R2_PUBLIC_ASSETS_URL}/banners/Promotions/{banner-file}`. An already absolute banner URL is returned unchanged.
- The response returns the public image URL; it does not proxy the image binary through this API.
- Device model, channel code, Promotion description, and Gifts are used or resolved internally as needed but are not included in the response. The queried IMEI is included in the response.

Success response: `200 OK`

```json
{
  "imei": "123456789012345",
  "promotions": [
    {
      "id": 123,
      "name": "Example Promotion",
      "bannerUrl": "https://assets.example.com/banners/Promotions/banner-uuid.webp"
    }
  ]
}
```

An existing Device with no applicable Promotions returns `200 OK` with an empty `promotions` array.

Invalid or missing IMEI response: `400 Bad Request`

Unknown IMEI response: `404 Not Found`

Database or configuration failure response: `503 Service Unavailable`

### Create Promotion

```http
POST /api/promotions
Content-Type: multipart/form-data
```

Creates a promotion, uploads its files to Cloudflare R2, and inserts all related database records in one MySQL transaction.

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
- `Promotions.slug_url` is generated from the promotion name as `/promotions/{name-slug}-{unique-suffix}` and checked against existing promotion slugs before insertion.
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
  "slugUrl": "/promotions/oppo-buds3-pro-for-a6-5g-spark-only-a1b2c3d4",
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
    "slugUrl": "/promotions/existing-promotion-a1b2c3d4"
  },
  "overlappingChannelCodes": ["SPK"]
}
```

Non-multipart request response: `415 Unsupported Media Type`

Database or R2 failure response: `503 Service Unavailable`

### Create Claim

```http
POST /api/claims
Content-Type: multipart/form-data
```

Creates a customer and claim, records its gifts and delivery address, uploads the receipt and screenshot to Cloudflare R2, and marks the claimed device as redeemed in one database transaction.

Multipart form fields:

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `promotionId` | Integer | Yes | ID of the selected existing Promotion. The backend reads its name for the R2 folder. |
| `imei` | Text | Yes | A 15-character Device IMEI. |
| `purchaseDate` | Text | Yes | Purchase date in `yyyy-MM-dd` format. |
| `firstName` | Text | Yes | Customer first name. |
| `lastName` | Text | Yes | Customer last name. |
| `email` | Text | Yes | Customer email address. |
| `contact` | Text | Yes | Customer contact number. |
| `street` | Text | Yes | Delivery street. |
| `suburb` | Text | Yes | Delivery suburb. |
| `city` | Text | Yes | Delivery city. |
| `postcode` | Text | Yes | Delivery postcode. |
| `instructions` | Text | No | Optional delivery instructions. |
| `giftAliases` | JSON array text | Yes | One or more unique Gift aliases selected from the Promotion. |
| `receipt` | File | Yes | Non-empty receipt file. |
| `screenshot` | File | Yes | Non-empty screenshot file. |

Example `giftAliases` value:

```json
["ENCO BUDS3PRO WHITE"]
```

Validation and persistence rules:

- The Promotion must exist.
- The IMEI must exist in `Devices`, must not already be redeemed, and its model must exist in `Promotion_Devices` for the selected Promotion.
- The Device channel must exist in `Promotion_Channels`, and `purchaseDate` must be within that channel's `start_date` and `end_date`.
- Every Gift alias is resolved to `Gifts.id` and must exist in `Promotion_Gifts` for the selected Promotion.
- A new `Customers` row is inserted first and its generated ID is stored in `Claims.customer_id`.
- The Claim ID format is `OPNZPROCLM-yyMMdd-XXXXXXXX`, using the current `Pacific/Auckland` date. The final eight characters are cryptographically generated uppercase letters or digits, and the generated ID is checked against `Claims.id` before use.
- Receipt and screenshot files are independently renamed to UUID filenames while retaining safe extensions.
- Both files are uploaded under `claims/promotions/{promotion-name}/{uuid}.{extension}`. Slash characters and control characters in the Promotion name are replaced with `-` for a safe R2 object key.
- `Claims.receipt_url` and `Claims.screenshot_url` store the corresponding public R2 URLs.
- `Claims.status` and `Claims.email_status` initially use `0`.
- Selected Gifts are inserted into `Claim_Gifts`, and the delivery address is inserted into the existing `Deliver_Addresses` table with `is_current = 1`.
- After all Claim records are prepared, `Devices.redemption_status` is changed from `0` to `1` in the same transaction.
- If any database operation fails, the transaction is rolled back and files uploaded by the request are removed from R2.

Success response: `201 Created`

```json
{
  "id": "OPNZPROCLM-260827-4EUZB66Y",
  "promotionId": 123,
  "customerId": 456,
  "imei": "123456789012345",
  "giftIds": [12],
  "receiptUrl": "https://assets.example.com/claims/promotions/Example%20Promotion/uuid.pdf",
  "screenshotUrl": "https://assets.example.com/claims/promotions/Example%20Promotion/uuid.png"
}
```

Validation failure response: `400 Bad Request`

Already redeemed or concurrently claimed Device response: `409 Conflict`

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

- `category` must be `11` or `21`.
- `redemption_status` must be `0`.
- Duplicate results are merged by `market_name + model`.

Example request:

```http
GET /api/devices/search?market_name=Tem
```

Success response: `200 OK`

```json
[
  {
    "market_name": "Temp",
    "model": "CPH2689"
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
