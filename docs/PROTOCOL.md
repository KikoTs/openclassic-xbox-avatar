# Multiplayer avatar protocol

Avatar synchronization is capability-gated. A modded peer sends avatar data
only after another peer acknowledges support; unmodded clients continue using
the stock proxy character.

The avatar message class is registered after the stock Castle Miner Z message
types so existing packet IDs are not shifted. Avatar packets are consumed by
the add-on before the stock message dispatcher sees them.

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

The capability hello is queued until the game has assigned the local network
gamer. A reciprocal hello handles the case where the first handshake arrived
before a joining client was ready.
