## Plan: Channel Message Search System

**TL;DR**: Три новых API-метода для поиска сообщений в канале, построенные на CockroachDB FTS (tsvector/GIN) с архитектурой, готовой к замене на Meilisearch. Поиск возвращает только ID + snippet + токен, батч-фетч достаёт полные сообщения по токену, телепорт прыгает на сообщение с контекстом.

---

### Steps

#### Phase 1: Database Layer

**1. Миграция: tsvector + inverted index**
- Добавить вычисляемую колонку `TextSearchVector tsvector GENERATED ALWAYS AS (to_tsvector('simple', "Text")) STORED`
- Создать inverted index: `("SpaceId", "ChannelId", "TextSearchVector")` — CockroachDB поддерживает multi-column inverted indexes
- Словарь `'simple'` — без стемминга, работает для всех языков
- Файлы: новая миграция + обновить `Configure()` в `src/Argon.Core/Entities/Data/ArgonMessageEntity.cs`

**2. Интерфейс `IMessageSearchService`** *(параллельно с 1)*
- Абстракция для будущей замены на Meilisearch
- Методы: `SearchMessagesAsync()`, `TeleportAsync()`, `FetchByIdsAsync()`
- Новый файл: `src/Argon.Core/Services/IMessageSearchService.cs`

**3. Реализация `CockroachDbMessageSearchService`** *(зависит от 2)*
- **Search**: Raw SQL с `plainto_tsquery('simple', query)`, `ts_rank()` для сортировки, `ts_headline()` для сниппетов
- **Teleport**: Два запроса (`MessageId >= target ASC LIMIT after+1` + `MessageId < target DESC LIMIT before`), merge + sort
- **FetchByIds**: `WHERE MessageId IN (...)`, cap 50 IDs
- Новый файл: `src/Argon.Core/Features/CoreLogic/Messages/CockroachDbMessageSearchService.cs`

**4. DI регистрация** *(зависит от 3)*
- В `MessagesLayoutExtensions.AddMessagesLayout()` добавить `AddScoped<IMessageSearchService, CockroachDbMessageSearchService>`
- Файл: `src/Argon.Core/Features/CoreLogic/Messages/MessageProcessor.cs`

#### Phase 2: Access Token *(параллельно с Phase 1)*

**5. `ISearchTokenService` — HMAC-SHA256 stateless токен**
- **Generate**: `CreateToken(userId, spaceId, channelId)` → `{base64(hmac)}.{expiresUnixSeconds}`
- Payload: `{userId}|{spaceId}|{channelId}|{expires}`, подпись ключом из конфига
- TTL: 5 минут
- **Validate**: парсинг expiry + `CryptographicOperations.FixedTimeEquals` для constant-time сравнения
- Stateless — без Redis/DB
- Новый файл: `src/Argon.Core/Services/SearchTokenService.cs`

#### Phase 3: ION Transport

**6. ION типы и методы сервиса** *(зависит от 2)*
- Файл: `src/Argon.Ion/ChannelInteraction.ion`
- Новые типы:
  - `MessageSearchHit { messageId: i8, snippet: string }`
  - `MessageSearchResult { hits: MessageSearchHit[], token: string, totalEstimate: i4 }`
  - `MessageTeleportResult { messages: ArgonMessage[], targetIndex: i4 }`
- Новые методы в `service ChannelInteraction`:
  - `SearchMessages(query: string, limit: i4, offset: i4): MessageSearchResult`
  - `TeleportToMessage(messageId: i8, before: i4, after: i4): MessageTeleportResult`
  - `FetchMessagesBatch(messageIds: i8[], token: string): ArgonMessage[]`

**7. Regenerate ION codegen** *(зависит от 6)*

#### Phase 4: Grain Layer

**8. Методы в `IChannelGrain`** *(зависит от 2)*
- Файл: `src/Argon.Core/Grains/Interfaces/IChannelGrain.cs`
- `SearchMessages`, `TeleportToMessage`, `FetchMessagesByIds`

**9. Реализация в `ChannelGrain`** *(зависит от 3, 5, 8)*
- Инжектить `IMessageSearchService` + `ISearchTokenService`
- Search: проверка `ChannelType.Text` → вызов search → генерация токена
- Teleport: вызов → определение `targetIndex`
- FetchByIds: прямой вызов
- Файл: `src/Argon.Api/Grains/ChannelGrain.cs`

#### Phase 5: Service Router

**10. Реализация в `ChannelInteractionImpl`** *(зависит от 7, 9)*
- Роутинг к грейнам (по паттерну существующего `QueryMessages`)
- **Важно**: валидация токена в `FetchMessagesBatch` происходит **здесь** (interaction layer), не в грейне — потому что interaction layer владеет auth context
- Файл: `src/Argon.Core/Services/Ion/ChannelInteractionImpl.cs`

#### Phase 6: Metrics *(параллельно с любым шагом)*

**11. Инструментация**: `SearchQueries`, `SearchDuration`, `TeleportRequests`, `BatchFetchRequests`

---

### Relevant Files
- `src/Argon.Core/Entities/Data/ArgonMessageEntity.cs` — tsvector column + inverted index config
- `src/Argon.Core/Features/CoreLogic/Messages/MessageProcessor.cs` — DI, reference pattern
- `src/Argon.Ion/ChannelInteraction.ion` — ION types + methods
- `src/Argon.Core/Grains/Interfaces/IChannelGrain.cs` — grain interface
- `src/Argon.Api/Grains/ChannelGrain.cs` — grain implementation
- `src/Argon.Core/Services/Ion/ChannelInteractionImpl.cs` — routing

---

### Decisions
- **Scope**: только по каналу, не по space
- **`'simple'` dictionary** — мультиязычный, без стемминга
- **Token TTL**: 5 мин, stateless HMAC-SHA256
- **Batch limit**: 50 IDs max
- **Teleport**: возвращает полные `ArgonMessage[]` (не IDs), так как телепорт — для рендеринга
- **Мейлисерч позже**: `IMessageSearchService` — точка замены, zero changes в грейнах/ION

### Further Considerations
1. **Разделители в сниппетах**: `ts_headline` по умолчанию `<b>...</b>`. Рекомендация: использовать кастомные `StartSel='<<'` / `StopSel='>>'`, клиент сам парсит.
2. **DM search**: интерфейс `IMessageSearchService` расширяем перегрузками с `ConversationId` вместо `(SpaceId, ChannelId)`.
3. **Миграция на Meilisearch**: создать `MeilisearchMessageSearchService`, подписаться на `MessageSent` для индексации, swap DI.
