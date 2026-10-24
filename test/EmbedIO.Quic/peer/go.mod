module embedio-neo-quic-prototype-peer

go 1.26.0

// The Go release the peer Dockerfile builds with; vulnerability scanners read the
// standard library version from this line.
toolchain go1.27.2

require (
	github.com/quic-go/qpack v0.6.0
	github.com/quic-go/quic-go v0.63.0
)

require (
	golang.org/x/crypto v0.58.0 // indirect
	golang.org/x/net v0.61.0 // indirect
	golang.org/x/sys v0.49.0 // indirect
)
