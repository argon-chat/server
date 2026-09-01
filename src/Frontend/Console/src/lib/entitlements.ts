export interface EntitlementInfo {
  bit: bigint
  key: string
  label: string
  description: string
  category: EntitlementCategory
  dangerous?: boolean
}

export type EntitlementCategory = 'base' | 'chat' | 'media' | 'extended' | 'moderation' | 'admin'

export const categoryLabels: Record<EntitlementCategory, string> = {
  base: 'Base',
  chat: 'Chat & Messaging',
  media: 'Voice & Media',
  extended: 'Extended',
  moderation: 'Moderation',
  admin: 'Administration',
}

export const categoryOrder: EntitlementCategory[] = [
  'base', 'chat', 'media', 'extended', 'moderation', 'admin',
]

export const allEntitlements: EntitlementInfo[] = [
  // Base
  { bit: 1n << 0n,  key: 'ViewChannel',  label: 'View Channels',    description: 'Allows the bot to see channels',        category: 'base' },
  { bit: 1n << 1n,  key: 'ReadHistory',  label: 'Read History',     description: 'Allows reading message history',         category: 'base' },
  { bit: 1n << 2n,  key: 'JoinToVoice',  label: 'Join Voice',       description: 'Allows joining voice channels',          category: 'base' },

  // Chat
  { bit: 1n << 5n,  key: 'SendMessages',      label: 'Send Messages',       description: 'Allows sending messages in text channels',    category: 'chat' },
  { bit: 1n << 6n,  key: 'SendVoice',          label: 'Send Voice Messages', description: 'Allows sending voice messages',               category: 'chat' },
  { bit: 1n << 7n,  key: 'AttachFiles',         label: 'Attach Files',        description: 'Allows uploading files and images',           category: 'chat' },
  { bit: 1n << 8n,  key: 'AddReactions',        label: 'Add Reactions',       description: 'Allows adding reactions to messages',         category: 'chat' },
  { bit: 1n << 9n,  key: 'AnyMentions',         label: 'Mention Users',       description: 'Allows mentioning users and roles',           category: 'chat' },
  { bit: 1n << 10n, key: 'MentionEveryone',     label: 'Mention Everyone',    description: 'Allows using @everyone and @here',            category: 'chat', dangerous: true },
  { bit: 1n << 11n, key: 'ExternalEmoji',       label: 'External Emoji',      description: 'Allows using emoji from other spaces',        category: 'chat' },
  { bit: 1n << 12n, key: 'ExternalStickers',    label: 'External Stickers',   description: 'Allows using stickers from other spaces',     category: 'chat' },
  { bit: 1n << 13n, key: 'UseCommands',         label: 'Use Commands',        description: 'Allows using bot and slash commands',         category: 'chat' },
  { bit: 1n << 14n, key: 'PostEmbeddedLinks',   label: 'Embed Links',         description: 'Allows links to show embedded previews',      category: 'chat' },

  // Media
  { bit: 1n << 20n, key: 'Connect', label: 'Connect to Voice',  description: 'Allows connecting to voice channels',     category: 'media' },
  { bit: 1n << 21n, key: 'Speak',   label: 'Speak',             description: 'Allows speaking in voice channels',       category: 'media' },
  { bit: 1n << 22n, key: 'Video',   label: 'Video',             description: 'Allows sharing video in voice channels',  category: 'media' },
  { bit: 1n << 23n, key: 'Stream',  label: 'Screen Share',      description: 'Allows screen sharing in voice channels', category: 'media' },

  // Extended
  { bit: 1n << 30n, key: 'UseASIO',          label: 'Use ASIO',          description: 'Allows using low-latency ASIO audio',    category: 'extended' },
  { bit: 1n << 31n, key: 'AdditionalStreams', label: 'Additional Streams', description: 'Allows using additional media streams', category: 'extended' },

  // Moderation
  { bit: 1n << 40n, key: 'DisconnectMember', label: 'Disconnect Members', description: 'Allows disconnecting users from voice', category: 'moderation', dangerous: true },
  { bit: 1n << 41n, key: 'MoveMember',       label: 'Move Members',       description: 'Allows moving users between channels',  category: 'moderation', dangerous: true },
  { bit: 1n << 42n, key: 'BanMember',        label: 'Ban Members',        description: 'Allows banning users from the space',   category: 'moderation', dangerous: true },
  { bit: 1n << 43n, key: 'MuteMember',       label: 'Mute Members',       description: 'Allows muting users in voice channels', category: 'moderation', dangerous: true },
  { bit: 1n << 44n, key: 'KickMember',       label: 'Kick Members',       description: 'Allows kicking users from the space',   category: 'moderation', dangerous: true },

  // Admin
  { bit: 1n << 50n, key: 'ManageChannels',  label: 'Manage Channels',    description: 'Allows creating, editing, and deleting channels', category: 'admin', dangerous: true },
  { bit: 1n << 51n, key: 'ManageArchetype', label: 'Manage Archetypes',  description: 'Allows creating, editing, and deleting roles',    category: 'admin', dangerous: true },
  { bit: 1n << 52n, key: 'ManageBots',      label: 'Manage Bots',        description: 'Allows managing bot installations',              category: 'admin', dangerous: true },
  { bit: 1n << 53n, key: 'ManageEvents',    label: 'Manage Events',      description: 'Allows creating and managing events',             category: 'admin', dangerous: true },
  { bit: 1n << 54n, key: 'ManageBehaviour', label: 'Manage Behaviour',   description: 'Allows configuring automod and behaviour rules',  category: 'admin', dangerous: true },
  { bit: 1n << 55n, key: 'ManageServer',    label: 'Manage Server',      description: 'Allows full server management',                  category: 'admin', dangerous: true },
]

/** Convert a u64 mask (number) to the set of matching entitlements. */
export function entitlementsFromMask(mask: number | bigint): EntitlementInfo[] {
  const m = BigInt(mask)
  return allEntitlements.filter(e => (m & e.bit) !== 0n)
}

/** Build a u64 mask from an array of entitlement keys. */
export function maskFromKeys(keys: string[]): bigint {
  const set = new Set(keys)
  return allEntitlements
    .filter(e => set.has(e.key))
    .reduce((acc, e) => acc | e.bit, 0n)
}

/** Group entitlements by category, preserving category order. */
export function groupedEntitlements(): { category: EntitlementCategory; label: string; items: EntitlementInfo[] }[] {
  return categoryOrder.map(cat => ({
    category: cat,
    label: categoryLabels[cat],
    items: allEntitlements.filter(e => e.category === cat),
  }))
}
