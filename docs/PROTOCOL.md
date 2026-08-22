# Multiplayer avatar protocol

Avatar synchronization is capability-gated. A peer advertises support by
appending a versioned `OCXACAP` marker to a duplicate stock
`PlayerExistsMessage`. The add-on strips the marker before normal game
processing. Vanilla and non-OpenClassic peers understand the message itself
and safely ignore it as a duplicate, then continue using the stock proxy
character.

The marker has two forms. The original eight bytes are `OCXACAP` followed by
protocol version `1`. Current builds send nine: `OCXACAP`, form `2`, and the
byte ID the sender registered the avatar message under. Both are accepted;
a first-form peer is treated as it always was.

The avatar message class is registered after the stock Castle Miner Z message
types so existing packet IDs are not shifted - IDs are positional, assigned to
every message type in the process sorted by name, and the avatar message sorts
last. A client that registers further message types of its own can move that
ID, and a packet sent to it would be decoded as whatever type holds that
number there. The second marker form exists so that can be seen before any
packet goes out: a peer whose advertised ID differs from ours is left on the
stock model and the mismatch is logged. Avatar packets are consumed by the
add-on before the stock message dispatcher sees them. Every custom send path
has a final capability check, so an unknown packet ID is never sent to an
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

Outgoing chunks are served round-robin across every transfer in progress, so
a full lobby joining at once does not leave the last player waiting through
everyone else's avatar. A receiver allows 180 seconds for the first chunk of
an accepted manifest and 45 seconds between chunks after that. A manifest is
accepted only from a peer we said hello to, at most once every five seconds
per peer, and a hello from a peer that already has an offer or transfer in
flight is ignored.

The engine never marks a `NetworkGamer` as having left, so the add-on judges
that itself: a remote gamer is gone when it is no longer in the session's
gamer list, or the session has been disposed. Departed players' models and
state are released within five seconds; leaving a session releases every
remote player and drops every queued transfer.
