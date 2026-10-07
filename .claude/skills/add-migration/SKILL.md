---
name: add-migration
description: Add an EF Core migration after changing entities or ApplicationDbContext. Use whenever a Models/*.cs entity or the DbContext model changes.
---

# Add an EF Core migration

```bash
cd src/Notify.Web
dotnet ef migrations add <DescriptiveName> -o Data/Migrations
dotnet build
```

Rules:

- Migrations are applied automatically at startup by `db.Database.Migrate()` in `Program.cs`; never add a manual
  `dotnet ef database update` step to docs or scripts.
- `dotnet ef migrations add` compiles the project **before** writing the migration files. Always run `dotnet build`
  afterwards; otherwise `dotnet run --no-build` starts with a DLL that lacks the migration and the tables are missing.
- The provider is SQLite. Avoid operations SQLite cannot do in place (dropping or altering columns requires a table
  rebuild, which EF handles but review the generated code).
- Entities that belong to a tenant must carry `UserId` and be configured with a cascade delete from
  `ApplicationUser` in `ApplicationDbContext.OnModelCreating`, matching `Campaign` and `SmtpSetting`.
- Commit the migration together with the updated `ApplicationDbContextModelSnapshot.cs`.
