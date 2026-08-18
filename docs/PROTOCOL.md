# Multiplayer avatar protocol

Avatar synchronization is capability-gated. A peer advertises support by
appending an eight-byte, versioned `OCXACAP` marker to a duplicate stock
`PlayerExistsMessage`. The add-on strips the marker before normal game
processing. Vanilla and non-OpenClassic peers understand the message itself
and safely ignore it as a duplicate, then continue using the stock proxy
character.

The avatar message class is registered after the stock Castle Miner Z message
types so existing packet IDs are not shifted. Avatar packets are consumed by
the add-on before the stock message dispatcher sees them. Every custom send
path has a final capability check, so an unknown packet ID is never sent to an
unmodified or incompatible peer.

Transfers use reliable chunks with:

- protocol version and packet kind;
- transfer identifier;
- total file length;
- chunk index and chunk count;
- SHA-256 content hash;
- a payload of at most 3,000 bytes.

The receiver rejects assets larger than 4 MiB, invalid lengths or chunk
counts, duplicate/conflicting transfers, timeouts, truncation, and hash
mismatches. Valid assets are cached by content hash under
`OpenClassic Addons/Xbox Avatar/Cache`.

The stock-safe advertisement is queued until the game has assigned the local
network gamer and is repeated when a player joins. A custom hello is queued
only after the matching remote gamer has supplied the exact marker and
protocol version. Gamer-ID reuse clears all previous capability state.
