# sosa-fam-api

The API that every Sosa Fam app talks to: the family data-sharing app, the food storage app, and anything that comes later. It runs on ASP.NET Core 10 with Entity Framework Core and uses the SQL Server database defined in [sosa-fam-database](https://github.com/SosaNuvol/sosa-fam-database).

## What is here so far

- **Sign-in:** email and password through ASP.NET Core Identity. The API returns a 30-minute JWT access token and a 30-day refresh token. Each refresh token works only once, and the API stores only a hash of it. Apple and Google sign-in come next.
- **Households:** create one, rename it, delete it, and list the households you belong to.
- **Members:** list them, change roles (Owner, Adult, Member, Child), remove someone, or leave.
- **Grocery lists:** shared lists whose items are tagged by store (WinCo, Costco, ...), with a check-off that everyone watching sees right away.
- **Food storage (pantry):** locations (Pantry, Fridge, Basement, ...), products with minimum and target levels, per-location stock with +/- deltas, moves, fridge Have / Low / Out, a restock list that feeds the grocery list, put-away after shopping, and a history of changes.
- **Invites:** an Owner or Adult creates an 8-character code such as `K7QM-2XPA`. It works once and expires after 7 days by default. The API stores only its hash, and guesses are rate-limited.

## How households are kept apart

Three layers do this, and each one works even if the others have a bug:

1. **Routes.** Every household route is `/households/{householdId}/...`. A filter checks that you are a member and returns 404 if you are not, so nobody can probe for other households.
2. **EF Core.** Any entity that implements `IHouseholdScoped` is filtered automatically to the current household. Saving a row for a different household throws an error.
3. **SQL Server row-level security.** `SessionContextInterceptor` copies the user and household into `SESSION_CONTEXT`, marked read-only, every time a connection opens. The database's security policy then hides and blocks other households' rows. `RowLevelSecurityTests` proves this layer holds on its own.

Only two operations cross households: creating a household and accepting an invite. Both run inside `TenantContext.BeginSystemScope()` and do their own checks.

### Adding tables for an app

1. Add the table and its row-level security in sosa-fam-database (see its README).
2. Make the entity implement `IHouseholdScoped`. Map it in `AppDbContext` and the household filter applies automatically.
3. Put the endpoints under `app.MapHouseholdGroup()` and use `tenant.RequireRole(...)` where a role matters.
4. Add a test that a second household cannot see or change the rows.

## Endpoints

| Method | Route | Who |
| --- | --- | --- |
| POST | `/auth/register`, `/auth/login`, `/auth/refresh`, `/auth/logout` | Anyone (rate-limited) |
| GET | `/me` | Signed in |
| GET, POST | `/households` | Signed in |
| GET | `/households/{id}` | Member |
| PATCH | `/households/{id}` | Owner, Adult |
| DELETE | `/households/{id}` | Owner |
| GET | `/households/{id}/members` | Member |
| PATCH | `/households/{id}/members/{memberId}` | Rename yourself; Owner and Adult can change others and roles |
| DELETE | `/households/{id}/members/{memberId}` | Leave yourself; Owner and Adult can remove others |
| GET, POST | `/households/{id}/invites` | Owner, Adult |
| DELETE | `/households/{id}/invites/{inviteId}` | Owner, Adult |
| POST | `/invites/accept` | Signed in (rate-limited) |
| GET, POST | `/households/{id}/stores` | Member |
| PATCH | `/households/{id}/stores/{storeId}` | Member |
| DELETE | `/households/{id}/stores/{storeId}` | Owner, Adult (its items become "any store") |
| GET, POST | `/households/{id}/grocery-lists` (`?archived=true` for archived) | Member |
| GET, PATCH | `/households/{id}/grocery-lists/{listId}` (rename, archive) | Member |
| DELETE | `/households/{id}/grocery-lists/{listId}` | Whoever created it, Owner, Adult |
| POST | `/households/{id}/grocery-lists/{listId}/clear-checked` | Member |
| POST | `/households/{id}/grocery-lists/{listId}/items` | Member |
| PUT, DELETE | `/households/{id}/grocery-lists/{listId}/items/{itemId}` | Member |
| PUT | `/households/{id}/grocery-lists/{listId}/items/{itemId}/checked` | Member (children too) |
| GET, POST | `/households/{id}/pantry/locations` (GET creates Pantry, Fridge, Basement the first time) | Member |
| PATCH | `/households/{id}/pantry/locations/{locationId}` | Member |
| DELETE | `/households/{id}/pantry/locations/{locationId}` | Owner, Adult |
| GET, POST | `/households/{id}/pantry/products` (`?search=`, `?category=`, `?needsRestock=true`) | Member |
| GET, PUT, DELETE | `/households/{id}/pantry/products/{productId}` | Member |
| GET | `/households/{id}/pantry/products/by-barcode/{barcode}` (404 when unknown) | Member |
| POST | `/households/{id}/pantry/products/{productId}/stock/{locationId}/adjust` (`{ delta }`, Count locations) | Member |
| PUT | `/households/{id}/pantry/products/{productId}/stock/{locationId}` (`{ quantity }` or `{ status }`) | Member |
| POST | `/households/{id}/pantry/products/{productId}/move` | Member |
| GET | `/households/{id}/pantry/needs` | Member |
| POST | `/households/{id}/pantry/needs/add-to-list` (`{ listId, productIds? }`) | Member |
| POST | `/households/{id}/pantry/put-away` (`{ groceryItemId, locationId, quantity? }`) | Member |
| GET | `/households/{id}/pantry/activity` (`?take=50`) | Member |

Pantry ids are Proxy-IDs such as `PAPR:31773:<guid>` (see the database repo's `.github/ProxyID-PrefixRegistry.md`); integer keys never leave the API. A grocery item's `productId` is a product's `groceryProductId` (the GUID segment of its Proxy-ID).

## Live updates

SignalR hub at `/hubs/household`. Connect with the access token (SignalR clients send it as the `access_token` query string), then call `JoinHousehold(householdId)`. Non-members get an error. Events:

| Event | Arguments |
| --- | --- |
| `groceryListsChanged` | none: reload the lists |
| `groceryItemUpserted` | `listId`, the item (same shape as the REST API) |
| `groceryItemDeleted` | `listId`, `itemId` |
| `pantryLocationsChanged` | none: reload the locations |
| `pantryProductsChanged` | none: reload the products |
| `pantryProductUpserted` | the product (same shape as the REST API, with its stock) |
| `pantryProductDeleted` | `productId` |

Other apps can send their own events through `IHubContext<HouseholdHub>` to `HouseholdHub.Group(householdId)`. Someone removed from a household keeps receiving its events until they reconnect.

Grocery items accept `source` (`Manual` or `LowStock`) and `productId`, so the food storage app can add low-stock products to a list.

In Development, Swagger UI is at `/swagger`.

## Run it locally

You need the .NET 10 SDK and a SQL Server database with the sosa-fam-database migrations applied.

```bash
cd src/SosaFam.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:SosaFam" "Server=<server>;Database=SosaFam;User Id=sosafam_api_login;Password=<password>;TrustServerCertificate=True"
dotnet user-secrets set "Jwt:SigningKey" "<random string, at least 32 characters>"
dotnet run
```

Keep secrets out of `appsettings.json`. On a server, set them as the environment variables `ConnectionStrings__SosaFam` and `Jwt__SigningKey`.

## Tests

The integration tests run the API in memory against a real SQL Server. They are skipped unless `SOSAFAM_TEST_DB` points to a migrated development database:

```bash
export SOSAFAM_TEST_DB="Server=localhost,1433;Database=SosaFam;User Id=sa;Password=<dev password>;TrustServerCertificate=True"
dotnet test
```

The tests create users and households with random names and leave them in place, so use a development database.

## Deploying to production

See [docs/Deploying-the-API.md](docs/Deploying-the-API.md): IIS on QUADITMAPP001 as `sosa-fam-api.nuvol9.com`, published with `deploy/Publish-SosaFamApiProd.ps1`. Its secrets are set with `deploy/Set-SosaFamApiProdSecret.ps1`, which only you run.
