# Angry Hauling

EVE Online jump freighter logistics web application. Alliance members can request items purchased and hauled from Jita to null-sec.

## Features

- EVE Online SSO authentication with alliance membership verification
- Item search from CCP's Static Data Export (18,000+ market items)
- Real-time Jita sell prices via ESI market API
- Automatic volume and fee calculation
- Order lifecycle management (Pending → Accepted → In Transit → Delivered)
- Role-based access: Members, JF Pilots, Admins
- Personal shopper mode with configurable fee

## Architecture

- **Frontend:** React SPA (Vite + TypeScript)
- **Backend:** .NET 10 AOT minimal API (C#)
- **Database:** PostgreSQL

## Project Structure

- `/api/` — .NET 10 AOT backend API
- `/web/` — React frontend (Vite + TypeScript)
- `/sql/` — Database schema scripts (source of truth)

## Building

### API
```
cd api/Hauling.Api
dotnet publish -c Release -r linux-x64 --self-contained
```

### Frontend
```
cd web
npm install && npm run build
```
