using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <summary>
    /// Profile cosmetics: the catalogue, who owns what, loadouts and what is equipped in them, which
    /// loadout a space sees, names as rows, and the key scenario that lets an inventory item unlock a
    /// cosmetic.
    /// </summary>
    public partial class Cosmetics : Migration
    {
        /// <summary>
        /// The catalogue rows every database starts with.
        /// </summary>
        /// <remarks>
        /// <para>Written as SQL in a migration rather than as <c>HasData</c> or a startup seeder, and
        /// both alternatives were wrong for the same reason: these rows belong to operators once they
        /// exist. <c>HasData</c> would make EF own them, so an operator renaming one would be reverted
        /// by the next migration; a startup seeder would run on every replica and need its own
        /// idempotence. A migration runs once per database, under the lease the boot path already
        /// takes.</para>
        ///
        /// <para><b>Backgrounds and badges</b> are the cosmetics that existed as hardcoded preset ids,
        /// given rows so <c>UserGrain</c> asks the catalogue instead of a <c>FrozenSet</c> in the
        /// source. The backgrounds are deliberately unpublished: their assets are five <c>.webm</c>
        /// files inside the client bundle, not in object storage, and an unpublished row still answers
        /// the legacy id through the projection's fallback. The badge slugs match the strings the two
        /// hardcoded badge renderers look for, plus premium, which the popover derives from a flag; a
        /// mismatch would show the same badge twice.</para>
        ///
        /// <para><b>Options</b> are what a nickname style is composed from: faces, colours and
        /// treatments. Published, because they have no assets to wait for: the faces are in the client
        /// bundle and a treatment is our own CSS, which is why <c>Payload</c> is empty for one. Free
        /// except two, because <c>AcquisitionMode</c> carries the free flag per row.</para>
        /// </remarks>
        private const string Catalogue = """
            INSERT INTO "Cosmetics" (
                "Id", "KindKey", "Slug", "NameKey", "DescriptionKey", "Rarity", "SortOrder", "Version",
                "Payload", "AssetFileIds", "AssetSource", "IsEnabled", "IsPublished", "AcquisitionMode",
                "LegacyId", "CreatedAt", "UpdatedAt", "IsDeleted")
            VALUES
                ('bbbbbbbb-0000-4000-8000-000000000001', 'profile.background', 'blackhole',  'cosmetic_background_blackhole',  NULL, 'rare', 1, 1, '{"loop":true,"tintOpacity":0.35}', '{}'::jsonb, 0, true, false, 5, 1, now(), now(), false),
                ('bbbbbbbb-0000-4000-8000-000000000002', 'profile.background', 'dream_city', 'cosmetic_background_dream_city', NULL, 'rare', 2, 1, '{"loop":true,"tintOpacity":0.35}', '{}'::jsonb, 0, true, false, 5, 2, now(), now(), false),
                ('bbbbbbbb-0000-4000-8000-000000000003', 'profile.background', 'rain',       'cosmetic_background_rain',       NULL, 'rare', 3, 1, '{"loop":true,"tintOpacity":0.35}', '{}'::jsonb, 0, true, false, 5, 3, now(), now(), false),
                ('bbbbbbbb-0000-4000-8000-000000000004', 'profile.background', 'sakura',     'cosmetic_background_sakura',     NULL, 'rare', 4, 1, '{"loop":true,"tintOpacity":0.35}', '{}'::jsonb, 0, true, false, 5, 4, now(), now(), false),
                ('bbbbbbbb-0000-4000-8000-000000000005', 'profile.background', 'shells',     'cosmetic_background_shells',     NULL, 'rare', 5, 1, '{"loop":true,"tintOpacity":0.35}', '{}'::jsonb, 0, true, false, 5, 5, now(), now(), false),
                ('bbbbbbbb-0001-4000-8000-000000000001', 'profile.badge', 'owner',       'cosmetic_badge_owner',       NULL, 'legendary', 1, 1, '{"tooltipKey":"badge_owner"}',       '{}'::jsonb, 0, true, false, 1, NULL, now(), now(), false),
                ('bbbbbbbb-0001-4000-8000-000000000002', 'profile.badge', 'staff',       'cosmetic_badge_staff',       NULL, 'legendary', 2, 1, '{"tooltipKey":"badge_staff"}',       '{}'::jsonb, 0, true, false, 1, NULL, now(), now(), false),
                ('bbbbbbbb-0001-4000-8000-000000000003', 'profile.badge', 'contributor', 'cosmetic_badge_contributor', NULL, 'rare',      3, 1, '{"tooltipKey":"badge_contributor"}', '{}'::jsonb, 0, true, false, 1, NULL, now(), now(), false),
                ('bbbbbbbb-0001-4000-8000-000000000004', 'profile.badge', 'premium',     'cosmetic_badge_premium',     NULL, 'rare',      4, 1, '{"tooltipKey":"badge_premium"}',     '{}'::jsonb, 0, true, false, 5, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000001', 'option.font', 'inter',         'cosmetic_font_inter',         NULL, NULL, 1, 1, '{"cssFamily":"Inter, sans-serif"}',         '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000002', 'option.font', 'roboto',        'cosmetic_font_roboto',        NULL, NULL, 2, 1, '{"cssFamily":"Roboto, sans-serif"}',        '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000003', 'option.font', 'lato',          'cosmetic_font_lato',          NULL, NULL, 3, 1, '{"cssFamily":"Lato, sans-serif"}',          '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000004', 'option.font', 'fira-sans',     'cosmetic_font_fira_sans',     NULL, NULL, 4, 1, '{"cssFamily":"''Fira Sans'', sans-serif"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000005', 'option.font', 'open-sans',     'cosmetic_font_open_sans',     NULL, NULL, 5, 1, '{"cssFamily":"''Open Sans'', sans-serif"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0002-4000-8000-000000000006', 'option.font', 'open-dyslexic', 'cosmetic_font_open_dyslexic', NULL, NULL, 6, 1, '{"cssFamily":"OpenDyslexic, sans-serif"}',  '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000001', 'option.swatch', 'rose',    'cosmetic_color_rose',    NULL, NULL, 1, 1, '{"hex":"#f43f5e"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000002', 'option.swatch', 'amber',   'cosmetic_color_amber',   NULL, NULL, 2, 1, '{"hex":"#f59e0b"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000003', 'option.swatch', 'emerald', 'cosmetic_color_emerald', NULL, NULL, 3, 1, '{"hex":"#10b981"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000004', 'option.swatch', 'cyan',    'cosmetic_color_cyan',    NULL, NULL, 4, 1, '{"hex":"#06b6d4"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000005', 'option.swatch', 'blue',    'cosmetic_color_blue',    NULL, NULL, 5, 1, '{"hex":"#3b82f6"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000006', 'option.swatch', 'indigo',  'cosmetic_color_indigo',  NULL, NULL, 6, 1, '{"hex":"#6366f1"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000007', 'option.swatch', 'violet',  'cosmetic_color_violet',  NULL, NULL, 7, 1, '{"hex":"#8b5cf6"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0003-4000-8000-000000000008', 'option.swatch', 'fuchsia', 'cosmetic_color_fuchsia', NULL, NULL, 8, 1, '{"hex":"#d946ef"}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0004-4000-8000-000000000001', 'option.text-effect', 'gradient', 'cosmetic_effect_gradient', NULL, NULL, 1, 1, '{}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0004-4000-8000-000000000002', 'option.text-effect', 'glow',     'cosmetic_effect_glow',     NULL, NULL, 2, 1, '{}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
                ('bbbbbbbb-0004-4000-8000-000000000003', 'option.text-effect', 'shimmer',  'cosmetic_effect_shimmer',  NULL, NULL, 3, 1, '{}', '{}'::jsonb, 0, true, true, 4,  NULL, now(), now(), false)
            ON CONFLICT DO NOTHING;
            """;

        /// <summary>
        /// Names for the rows above, in the two languages the catalogue shipped with.
        /// </summary>
        /// <remarks>
        /// A name is a row: <c>cosmeticName()</c> reads the wearer's locale out of
        /// <c>CosmeticTranslations</c>, falls back to <c>en</c>, and then prints the key verbatim. The
        /// keys beyond the catalogue's own rows name what a stand's seed script adds; on a database
        /// without those rows the join simply finds nothing to name.
        /// </remarks>
        private const string Translations = """
            WITH seed(key, en, ru) AS (VALUES
                ('cosmetic_background_blackhole',   'Black Hole',   'Чёрная дыра'),
                ('cosmetic_background_dream_city',  'Dream City',   'Город мечты'),
                ('cosmetic_background_rain',        'Rain',         'Дождь'),
                ('cosmetic_background_sakura',      'Sakura',       'Сакура'),
                ('cosmetic_background_shells',      'Shells',       'Ракушки'),
                ('cosmetic_badge_owner',            'Owner',        'Владелец'),
                ('cosmetic_badge_staff',            'Staff',        'Команда'),
                ('cosmetic_badge_contributor',      'Contributor',  'Контрибьютор'),
                ('cosmetic_badge_premium',          'Argon Ultima', 'Argon Ultima'),
                ('cosmetic_font_inter',             'Inter',        'Inter'),
                ('cosmetic_font_roboto',            'Roboto',       'Roboto'),
                ('cosmetic_font_lato',              'Lato',         'Lato'),
                ('cosmetic_font_fira_sans',         'Fira Sans',    'Fira Sans'),
                ('cosmetic_font_open_sans',         'Open Sans',    'Open Sans'),
                ('cosmetic_font_open_dyslexic',     'OpenDyslexic', 'OpenDyslexic'),
                ('cosmetic_color_rose',             'Rose',         'Розовый'),
                ('cosmetic_color_amber',            'Amber',        'Янтарный'),
                ('cosmetic_color_emerald',          'Emerald',      'Изумрудный'),
                ('cosmetic_color_cyan',             'Cyan',         'Бирюзовый'),
                ('cosmetic_color_blue',             'Blue',         'Синий'),
                ('cosmetic_color_indigo',           'Indigo',       'Индиго'),
                ('cosmetic_color_violet',           'Violet',       'Фиолетовый'),
                ('cosmetic_color_fuchsia',          'Fuchsia',      'Фуксия'),
                ('cosmetic_effect_gradient',        'Blend',        'Растяжка'),
                ('cosmetic_effect_glow',            'Glow',         'Свечение'),
                ('cosmetic_effect_shimmer',         'Sheen',        'Блик'),
                ('cosmetic_effect_snowfall',        'Snowfall',     'Снегопад'),
                ('cosmetic_effect_embers',          'Embers',       'Искры огня'),
                ('cosmetic_decoration_neon_ring',   'Neon ring',    'Неоновое кольцо'),
                ('cosmetic_decoration_sparks',      'Sparks',       'Искры'),
                ('cosmetic_decoration_orbit',       'Orbit',        'Орбита'),
                ('cosmetic_frame_gilded',           'Gilded',       'Золочёная'),
                ('cosmetic_frame_circuit',          'Circuit',      'Схема'),
                ('cosmetic_frame_thorns',           'Thornbound',   'Терновник'),
                ('cosmetic_widget_note',            'Note',         'Заметка'),
                ('cosmetic_widget_tags',            'Tags',         'Теги'),
                ('cosmetic_widget_picture',         'Picture',      'Картинка')
            ),
            descriptions(key, en, ru) AS (VALUES
                ('cosmetic_widget_note_desc',    'A heading and a few lines in your own words.',                 'Заголовок и несколько строк своими словами.'),
                ('cosmetic_widget_tags_desc',    'Short labels you choose for yourself.',                        'Короткие метки, которые вы выбираете сами.'),
                ('cosmetic_widget_picture_desc', 'A picture of your own, filling its card.',                     'Своя картинка во всю карточку.'),
                ('cosmetic_frame_thorns_desc',   'A briar grown around the card, with something perched on it.', 'Терновая лоза вокруг карточки, и кое-кто сидит на ней сверху.')
            ),
            rows(item_id, locale, name, description) AS (
                SELECT c."Id", l.locale,
                       CASE WHEN l.locale = 'en' THEN s.en ELSE s.ru END,
                       CASE WHEN l.locale = 'en' THEN d.en ELSE d.ru END
                FROM "Cosmetics" c
                JOIN seed s ON s.key = c."NameKey"
                LEFT JOIN descriptions d ON d.key = c."DescriptionKey"
                CROSS JOIN (VALUES ('en'), ('ru')) AS l(locale)
                WHERE c."IsDeleted" = false
            )
            INSERT INTO "CosmeticTranslations"
                ("Id", "CosmeticItemId", "Locale", "Name", "Description", "CreatedAt", "UpdatedAt", "IsDeleted")
            SELECT gen_random_uuid(), item_id, locale, name, description, now(), now(), false
            FROM rows
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "UsersToServerRelations",
                type: "text",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CosmeticDurationDays",
                table: "ItemUseScenario",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CosmeticId",
                table: "ItemUseScenario",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CosmeticLoadouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayNameOverride = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    DisplayNameChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvatarFileIdOverride = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    BioOverride = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticLoadouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticLoadouts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Cosmetics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KindKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    NameKey = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    DescriptionKey = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    Rarity = table.Column<string>(type: "text", maxLength: 32, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    AssetFileIds = table.Column<string>(type: "jsonb", nullable: false),
                    AssetSource = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShippedInClientAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShippedInClientBuild = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    AvailableFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcquisitionMode = table.Column<int>(type: "integer", nullable: false),
                    UltimaTierRequired = table.Column<int>(type: "integer", nullable: true),
                    PriceSku = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    GrantItemTemplateId = table.Column<string>(type: "text", maxLength: 255, nullable: true),
                    LegacyId = table.Column<int>(type: "integer", nullable: true),
                    MaxPerBoard = table.Column<int>(type: "integer", nullable: true),
                    BoardDefaultW = table.Column<int>(type: "integer", nullable: true),
                    BoardDefaultH = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cosmetics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticScopeAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoadoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticScopeAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticScopeAssignments_CosmeticLoadouts_LoadoutId",
                        column: x => x.LoadoutId,
                        principalTable: "CosmeticLoadouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CosmeticScopeAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticEquips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoadoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    KindKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    Overrides = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<string>(type: "text", nullable: true),
                    BoardX = table.Column<int>(type: "integer", nullable: false),
                    BoardY = table.Column<int>(type: "integer", nullable: false),
                    BoardW = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    BoardH = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticEquips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticEquips_CosmeticLoadouts_LoadoutId",
                        column: x => x.LoadoutId,
                        principalTable: "CosmeticLoadouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CosmeticEquips_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticOwnerships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    GiftedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticOwnerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticOwnerships_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CosmeticOwnerships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticTranslations_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticEquips_CosmeticItemId",
                table: "CosmeticEquips",
                column: "CosmeticItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticEquips_LoadoutId_KindKey_SlotIndex",
                table: "CosmeticEquips",
                columns: new[] { "LoadoutId", "KindKey", "SlotIndex" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticLoadouts_UserId",
                table: "CosmeticLoadouts",
                column: "UserId",
                unique: true,
                filter: "\"IsDefault\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticLoadouts_UserId_Name",
                table: "CosmeticLoadouts",
                columns: new[] { "UserId", "Name" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_CosmeticItemId",
                table: "CosmeticOwnerships",
                column: "CosmeticItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_ExpiresAt",
                table: "CosmeticOwnerships",
                column: "ExpiresAt",
                filter: "\"ExpiresAt\" IS NOT NULL AND \"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_UserId_CosmeticItemId",
                table: "CosmeticOwnerships",
                columns: new[] { "UserId", "CosmeticItemId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticScopeAssignments_LoadoutId",
                table: "CosmeticScopeAssignments",
                column: "LoadoutId");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticScopeAssignments_UserId",
                table: "CosmeticScopeAssignments",
                column: "UserId",
                unique: true,
                filter: "\"SpaceId\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticScopeAssignments_UserId_SpaceId",
                table: "CosmeticScopeAssignments",
                columns: new[] { "UserId", "SpaceId" },
                unique: true,
                filter: "\"SpaceId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticTranslations_CosmeticItemId_Locale",
                table: "CosmeticTranslations",
                columns: new[] { "CosmeticItemId", "Locale" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_IsPublished_AvailableUntil",
                table: "Cosmetics",
                columns: new[] { "IsPublished", "AvailableUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_KindKey_IsPublished_IsEnabled",
                table: "Cosmetics",
                columns: new[] { "KindKey", "IsPublished", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_KindKey_LegacyId",
                table: "Cosmetics",
                columns: new[] { "KindKey", "LegacyId" },
                unique: true,
                filter: "\"LegacyId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_KindKey_Slug",
                table: "Cosmetics",
                columns: new[] { "KindKey", "Slug" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.Sql(Catalogue);
            migrationBuilder.Sql(Translations);
        }

        /// <summary>
        /// The rows seeded above go with their tables.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CosmeticEquips");

            migrationBuilder.DropTable(
                name: "CosmeticOwnerships");

            migrationBuilder.DropTable(
                name: "CosmeticScopeAssignments");

            migrationBuilder.DropTable(
                name: "CosmeticTranslations");

            migrationBuilder.DropTable(
                name: "CosmeticLoadouts");

            migrationBuilder.DropTable(
                name: "Cosmetics");

            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "CosmeticDurationDays",
                table: "ItemUseScenario");

            migrationBuilder.DropColumn(
                name: "CosmeticId",
                table: "ItemUseScenario");
        }
    }
}
