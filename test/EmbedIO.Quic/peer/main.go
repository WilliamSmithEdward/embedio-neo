// Independent current-wire application peer. QUIC and TLS use quic-go's public APIs.
package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"github.com/quic-go/qpack"
	quic "github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/qlog"
	"io"
	"net"
	"os"
	"strconv"
	"time"
)

func integer(n uint64) []byte {
	width := 1
	if n >= 64 {
		width = 2
	}
	if n >= 16384 {
		width = 4
	}
	if n >= 1073741824 {
		width = 8
	}
	b := make([]byte, width)
	for i := width - 1; i >= 0; i-- {
		b[i] = byte(n)
		n >>= 8
	}
	b[0] |= map[int]byte{1: 0, 2: 64, 4: 128, 8: 192}[width]
	return b
}
func readInteger(r io.Reader) (uint64, error) {
	b := make([]byte, 1)
	if _, err := io.ReadFull(r, b); err != nil {
		return 0, err
	}
	width := 1 << (b[0] >> 6)
	n := uint64(b[0] & 63)
	for i := 1; i < width; i++ {
		if _, err := io.ReadFull(r, b); err != nil {
			return 0, err
		}
		n = n<<8 | uint64(b[0])
	}
	return n, nil
}
func frame(kind uint64, payload []byte) []byte {
	b := append(integer(kind), integer(uint64(len(payload)))...)
	return append(b, payload...)
}
func readFrame(r io.Reader) (uint64, []byte, error) {
	t, err := readInteger(r)
	if err != nil {
		return 0, nil, err
	}
	n, err := readInteger(r)
	if err != nil {
		return 0, nil, err
	}
	if n > 65536 {
		return 0, nil, errors.New("frame bound")
	}
	b := make([]byte, int(n))
	_, err = io.ReadFull(r, b)
	return t, b, err
}
func write(w io.Writer, b []byte) error {
	n, err := w.Write(b)
	if err == nil && n != len(b) {
		err = io.ErrShortWrite
	}
	return err
}
// requestAuthority is the :authority of session requests; a server on a
// non-default port expects that port in it.
var requestAuthority = "localhost"

func connectSession(ctx context.Context, conn *quic.Conn, token, origin string) (*quic.Stream, string, error) {
	stream, err := conn.OpenStreamSync(ctx)
	if err != nil {
		return nil, "", err
	}
	var block bytes.Buffer
	enc := qpack.NewEncoder(&block)
	for _, h := range []qpack.HeaderField{{Name: ":method", Value: "CONNECT"}, {Name: ":protocol", Value: "webtransport-h3"}, {Name: ":scheme", Value: "https"}, {Name: ":authority", Value: requestAuthority}, {Name: ":path", Value: "/echo"}, {Name: "origin", Value: origin}, {Name: "authorization", Value: "Bearer " + token}} {
		if err = enc.WriteField(h); err != nil {
			return nil, "", err
		}
	}
	if err = enc.Close(); err != nil {
		return nil, "", err
	}
	if err = write(stream, frame(1, block.Bytes())); err != nil {
		return nil, "", err
	}
	t, b, err := readFrame(stream)
	if err != nil {
		return nil, "", err
	}
	if t != 1 {
		return nil, "", errors.New("missing response HEADERS")
	}
	status := ""
	decode := qpack.NewDecoder().Decode(b)
	for {
		h, e := decode()
		if errors.Is(e, io.EOF) {
			break
		}
		if e != nil {
			return nil, "", e
		}
		if h.Name == ":status" {
			status = h.Value
		}
	}
	return stream, status, nil
}
func run() error {
	port := flag.Int("port", 0, "loopback port")
	ca := flag.String("ca", "", "test CA")
	mode := flag.String("mode", "echo", "case")
	authority := flag.String("authority", "localhost", ":authority of session requests")
	flag.Parse()
	requestAuthority = *authority
	pem, err := os.ReadFile(*ca)
	if err != nil {
		return err
	}
	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(pem) {
		return errors.New("invalid CA")
	}
	budget := 8 * time.Second
	if *mode == "endurance" {
		budget = 20 * time.Second
	}
	ctx, cancel := context.WithTimeout(context.Background(), budget)
	defer cancel()
	config := &quic.Config{EnableDatagrams: true, EnableStreamResetPartialDelivery: *mode != "missing-reset", MaxIncomingStreams: 16, MaxIncomingUniStreams: 16}
	// Diagnostics: qlog traces of the peer's connection, written to QLOGDIR when it is set.
	if os.Getenv("QLOGDIR") != "" {
		config.Tracer = qlog.DefaultConnectionTracer
	}
	if *mode == "idle" {
		config.MaxIdleTimeout = 500 * time.Millisecond
	}
	if *mode == "credit" || *mode == "sustained-credit" {
		config.InitialStreamReceiveWindow = 1024
		config.MaxStreamReceiveWindow = 2048
		config.InitialConnectionReceiveWindow = 8192
		config.MaxConnectionReceiveWindow = 16384
	}
	conn, err := quic.DialAddr(ctx, net.JoinHostPort("127.0.0.1", strconv.Itoa(*port)), &tls.Config{RootCAs: roots, ServerName: "localhost", NextProtos: []string{"h3"}, MinVersion: tls.VersionTLS13}, config)
	if err != nil {
		return err
	}
	defer conn.CloseWithError(0x100, "")
	state := conn.ConnectionState()
	if *mode != "missing-reset" && (!state.SupportsStreamResetPartialDelivery.Local || !state.SupportsStreamResetPartialDelivery.Remote) {
		return errors.New("current reset not negotiated")
	}
	if !state.SupportsDatagrams.Local || !state.SupportsDatagrams.Remote {
		return errors.New("datagrams not negotiated")
	}
	ready := make(chan error, 1)
	unis := make(chan *quic.ReceiveStream, 8)
	// Written before the successful ready send, which publishes it to run.
	var serverSettings map[uint64]uint64
	go func() {
		for {
			s, e := conn.AcceptUniStream(ctx)
			if e != nil {
				return
			}
			go func(s *quic.ReceiveStream) {
				t, e := readInteger(s)
				if e != nil {
					select {
					case ready <- e:
					default:
					}
					return
				}
				if t == 0 {
					kind, p, e := readFrame(s)
					if e != nil {
						ready <- e
						return
					}
					if kind != 4 {
						ready <- errors.New("missing SETTINGS")
						return
					}
					values := map[uint64]uint64{}
					r := bytes.NewReader(p)
					for r.Len() > 0 {
						id, e := readInteger(r)
						if e != nil {
							ready <- e
							return
						}
						v, e := readInteger(r)
						if e != nil {
							ready <- e
							return
						}
						values[id] = v
					}
					if values[8] != 1 || values[0x33] != 1 || values[0x2c7cf000] != 1 {
						ready <- errors.New("settings not negotiated")
						return
					}
					serverSettings = values
					ready <- nil
				} else if t == 0x54 {
					select {
					case unis <- s:
					case <-ctx.Done():
					}
				}
			}(s)
		}
	}()
	control, err := conn.OpenUniStreamSync(ctx)
	if err != nil {
		return err
	}
	settings := []byte{}
	for _, pair := range [][2]uint64{{0x33, 1}, {0x2c7cf000, 1}, {0x2b61, 65536}, {0x2b64, 8}, {0x2b65, 8}} {
		settings = append(settings, integer(pair[0])...)
		settings = append(settings, integer(pair[1])...)
	}
	if err = write(control, append([]byte{0}, frame(4, settings)...)); err != nil {
		return err
	}
	select {
	case err = <-ready:
		if err != nil {
			return err
		}
	case <-ctx.Done():
		return ctx.Err()
	}
	token := os.Getenv("EMBEDIO_QUIC_PROBE_TOKEN")
	origin := "https://localhost"
	if *mode == "unauthorized" {
		token = "incorrect"
	}
	if *mode == "origin" {
		origin = "https://untrusted.example"
	}
	session, status, err := connectSession(ctx, conn, token, origin)
	if *mode == "missing-reset" {
		var streamError *quic.StreamError
		if !errors.As(err, &streamError) || streamError.ErrorCode != 0x10e {
			return fmt.Errorf("missing reset negotiation must reject CONNECT with H3_MESSAGE_ERROR: %v", err)
		}
		return nil
	}
	if err != nil {
		return err
	}
	if *mode == "unauthorized" || *mode == "origin" {
		if status != "403" {
			return fmt.Errorf("rejection status %s", status)
		}
		_, err = io.ReadAll(session)
		return err
	}
	if status != "200" {
		return fmt.Errorf("CONNECT status %s", status)
	}
	if *mode == "connection-fault" || *mode == "datagram-fault" || *mode == "idle" {
		if *mode == "datagram-fault" {
			// The server answers this echo only after its handler has accepted the
			// session, so the fault below cannot race that acceptance.
			if err = echoOnce(ctx, conn, session); err != nil {
				return err
			}
			if err = conn.SendDatagram(integer(1 << 60)); err != nil {
				return err
			}
		}
		if *mode == "connection-fault" {
			if err = control.Close(); err != nil {
				return err
			}
		}
		select {
		case <-conn.Context().Done():
			cause := context.Cause(conn.Context())
			if *mode == "idle" {
				var timeout *quic.IdleTimeoutError
				if !errors.As(cause, &timeout) {
					return fmt.Errorf("expected idle timeout: %v", cause)
				}
			} else if *mode == "datagram-fault" {
				var failure *quic.ApplicationError
				if !errors.As(cause, &failure) || failure.ErrorCode != 0x33 {
					return fmt.Errorf("expected datagram error: %v", cause)
				}
			} else {
				var failure *quic.TransportError
				if !errors.As(cause, &failure) || failure.ErrorCode != 0x0a {
					return fmt.Errorf("expected protocol close: %v", cause)
				}
			}
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	if *mode == "sustained" || *mode == "sustained-credit" || *mode == "endurance" {
		size := 262144
		if *mode == "endurance" {
			size = 4194304
		}
		return sustained(ctx, conn, session, unis, size, serverSettings[0x2b61])
	}
	if *mode == "stream-cycle" {
		return cycles(ctx, conn, session, unis, serverSettings)
	}
	if *mode == "outbound-reset" {
		upload, e := conn.OpenUniStreamSync(ctx)
		if e != nil {
			return e
		}
		header := append(integer(0x54), integer(uint64(session.StreamID()))...)
		if e = write(upload, append(header, bytes.Repeat([]byte("pending"), 500)...)); e != nil {
			return e
		}
		select {
		case response := <-unis:
			sid, e := readInteger(response)
			if e != nil {
				return e
			}
			if sid != uint64(session.StreamID()) {
				return errors.New("reset stream lost session prefix")
			}
			if e = write(session, frame(0, frame(0x2843, []byte{0, 0, 0, 0}))); e != nil {
				return e
			}
			_, e = io.ReadAll(response)
			var reset *quic.StreamError
			if !errors.As(e, &reset) || !reset.Remote || reset.ErrorCode != 0x170d7b68 {
				return fmt.Errorf("expected remote session reset: %v", e)
			}
			if e = session.Close(); e != nil {
				return e
			}
			_, e = io.ReadAll(session)
			return e
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	if *mode == "reset" {
		reset, e := conn.OpenUniStreamSync(ctx)
		if e != nil {
			return e
		}
		head := append(integer(0x54), integer(uint64(session.StreamID()))...)
		if e = write(reset, head); e != nil {
			return e
		}
		reset.SetReliableBoundary()
		if e = write(reset, []byte("abandoned-tail")); e != nil {
			return e
		}
		reset.CancelWrite(0x1234)
	}
	sessions := []*quic.Stream{session}
	if *mode == "siblings" || *mode == "session-fault" {
		second, s, e := connectSession(ctx, conn, token, origin)
		if e != nil {
			return e
		}
		if s != "200" {
			return errors.New("second session rejected")
		}
		sessions = append(sessions, second)
		rejected, s, e := connectSession(ctx, conn, token, origin)
		if e != nil {
			return e
		}
		if s != "429" {
			return errors.New("session bound not enforced")
		}
		if _, e = io.ReadAll(rejected); e != nil {
			return e
		}
	}
	if *mode == "session-fault" {
		if e := write(sessions[0], frame(0, frame(0x190B4D3D, []byte{0xff}))); e != nil {
			return e
		}
		if _, e := io.ReadAll(sessions[0]); e == nil {
			return errors.New("malformed capsule did not reset its session")
		}
		sessions = sessions[1:]
	}
	for _, session = range sessions {
		stream, e := conn.OpenStreamSync(ctx)
		if e != nil {
			return e
		}
		prefix := append(integer(0x41), integer(uint64(session.StreamID()))...)
		repetitions := 350
		if *mode == "large" || *mode == "credit" {
			repetitions = 2000
		}
		payload := bytes.Repeat([]byte("stream-payload/"), repetitions)
		if e = write(stream, append(prefix, payload...)); e != nil {
			return e
		}
		if e = stream.Close(); e != nil {
			return e
		}
		echoed, e := io.ReadAll(stream)
		if e != nil {
			return e
		}
		if !bytes.Equal(echoed, payload) {
			return errors.New("bidirectional echo mismatch")
		}
		uni, e := conn.OpenUniStreamSync(ctx)
		if e != nil {
			return e
		}
		head := append(integer(0x54), integer(uint64(session.StreamID()))...)
		if e = write(uni, append(head, payload...)); e != nil {
			return e
		}
		if e = uni.Close(); e != nil {
			return e
		}
		select {
		case response := <-unis:
			sid, e := readInteger(response)
			if e != nil {
				return e
			}
			if sid != uint64(session.StreamID()) {
				return errors.New("unidirectional session mismatch")
			}
			echoed, e = io.ReadAll(response)
			if e != nil {
				return e
			}
			if !bytes.Equal(echoed, payload) {
				return errors.New("unidirectional echo mismatch")
			}
		case <-ctx.Done():
			return ctx.Err()
		}
		datagram := append(integer(uint64(session.StreamID())/4), []byte("datagram-payload")...)
		if e = conn.SendDatagram(datagram); e != nil {
			return e
		}
		received, e := conn.ReceiveDatagram(ctx)
		if e != nil {
			return e
		}
		if !bytes.Equal(received, datagram) {
			return errors.New("datagram association mismatch")
		}
		// Split one capsule across two HTTP DATA frames to exercise incremental parsing.
		capsule := frame(0x2843, []byte{0, 0, 0, 0})
		if e = write(session, frame(0, capsule[:2])); e != nil {
			return e
		}
		if e = write(session, frame(0, capsule[2:])); e != nil {
			return e
		}
		if e = session.Close(); e != nil {
			return e
		}
		if _, e = io.ReadAll(session); e != nil {
			return e
		}
	}
	return nil
}
// echoOnce sends a short payload on a new bidirectional session stream and checks the echo.
func echoOnce(ctx context.Context, conn *quic.Conn, session *quic.Stream) error {
	stream, err := conn.OpenStreamSync(ctx)
	if err != nil {
		return err
	}
	payload := []byte("accepted")
	prefix := append(integer(0x41), integer(uint64(session.StreamID()))...)
	if err = write(stream, append(prefix, payload...)); err != nil {
		return err
	}
	if err = stream.Close(); err != nil {
		return err
	}
	echoed, err := io.ReadAll(stream)
	if err != nil {
		return err
	}
	if !bytes.Equal(echoed, payload) {
		return errors.New("bidirectional echo mismatch")
	}
	return nil
}
// initialData is the server's advertised initial session data limit.
func sustained(ctx context.Context, conn *quic.Conn, session *quic.Stream, unis <-chan *quic.ReceiveStream, streamSize int, initialData uint64) error {
	const chunkSize = 16384
	maximum := initialData
	sent := uint64(0)
	consumed := uint64(0)
	for _, uni := range []bool{false, true} {
		var writer io.WriteCloser
		var reader io.Reader
		if uni {
			stream, err := conn.OpenUniStreamSync(ctx)
			if err != nil {
				return err
			}
			writer = stream
			if err = write(stream, append(integer(0x54), integer(uint64(session.StreamID()))...)); err != nil {
				return err
			}
		} else {
			stream, err := conn.OpenStreamSync(ctx)
			if err != nil {
				return err
			}
			writer, reader = stream, stream
			if err = write(stream, append(integer(0x41), integer(uint64(session.StreamID()))...)); err != nil {
				return err
			}
		}
		for offset := 0; offset < streamSize; offset += chunkSize {
			payload := make([]byte, chunkSize)
			for i := range payload {
				payload[i] = byte((offset + i) % 251)
			}
			for sent+chunkSize > maximum {
				kind, data, err := readFrame(session)
				if err != nil {
					return err
				}
				if kind != 0 {
					return errors.New("expected capsule DATA")
				}
				capsules := bytes.NewReader(data)
				for capsules.Len() > 0 {
					kind, data, err = readFrame(capsules)
					if err != nil {
						return err
					}
					if kind == 0x190B4D3D {
						limit, e := readInteger(bytes.NewReader(data))
						if e != nil {
							return e
						}
						if limit > maximum {
							maximum = limit
						}
					}
				}
			}
			if err := write(writer, payload); err != nil {
				return err
			}
			sent += uint64(len(payload))
			if reader == nil {
				select {
				case response := <-unis:
					id, err := readInteger(response)
					if err != nil {
						return err
					}
					if id != uint64(session.StreamID()) {
						return errors.New("sustained stream association")
					}
					reader = response
				case <-ctx.Done():
					return ctx.Err()
				}
			}
			actual := make([]byte, len(payload))
			if _, err := io.ReadFull(reader, actual); err != nil {
				return err
			}
			if !bytes.Equal(actual, payload) {
				return errors.New("sustained echo differs")
			}
			consumed += uint64(len(actual))
			if consumed%32768 == 0 {
				if err := write(session, frame(0, frame(0x190B4D3D, integer(consumed+65536)))); err != nil {
					return err
				}
			}
		}
		if err := writer.Close(); err != nil {
			return err
		}
		tail, err := io.ReadAll(reader)
		if err != nil {
			return err
		}
		if len(tail) != 0 {
			return errors.New("sustained echo has extra bytes")
		}
	}
	datagram := append(integer(uint64(session.StreamID())/4), []byte("after-sustained")...)
	if err := conn.SendDatagram(datagram); err != nil {
		return err
	}
	received, err := conn.ReceiveDatagram(ctx)
	if err != nil {
		return err
	}
	if !bytes.Equal(received, datagram) {
		return errors.New("sustained datagram differs")
	}
	if maximum <= 262144 {
		return errors.New("session receive credit was not replenished")
	}
	if err = write(session, frame(0, frame(0x2843, []byte{0, 0, 0, 0}))); err != nil {
		return err
	}
	if err = session.Close(); err != nil {
		return err
	}
	_, err = io.ReadAll(session)
	return err
}

// settings holds the server's advertised initial session limits.
func cycles(ctx context.Context, conn *quic.Conn, session *quic.Stream, unis <-chan *quic.ReceiveStream, settings map[uint64]uint64) error {
	limits := map[uint64]uint64{0x190B4D3D: settings[0x2b61], 0x190B4D3F: settings[0x2b65], 0x190B4D40: settings[0x2b64]}
	var sent, consumed uint64
	for _, uni := range []bool{false, true} {
		kind := uint64(0x190B4D3F)
		if uni {
			kind = 0x190B4D40
		}
		for index := uint64(0); index < 128; index++ {
			payload := bytes.Repeat([]byte{byte(index), byte(index >> 8), 7, 11}, 256)
			for index >= limits[kind] || sent+uint64(len(payload)) > limits[0x190B4D3D] {
				t, data, err := readFrame(session)
				if err != nil {
					return err
				}
				if t != 0 {
					return errors.New("cycle expected capsule DATA")
				}
				capsules := bytes.NewReader(data)
				for capsules.Len() > 0 {
					t, data, err = readFrame(capsules)
					if err != nil {
						return err
					}
					if t == 0x190B4D3D || t == 0x190B4D3F || t == 0x190B4D40 {
						value, err := readInteger(bytes.NewReader(data))
						if err != nil {
							return err
						}
						if value > limits[t] {
							limits[t] = value
						}
					}
				}
			}
			var reader io.Reader
			if uni {
				stream, err := conn.OpenUniStreamSync(ctx)
				if err != nil {
					return err
				}
				if err = write(stream, append(append(integer(0x54), integer(uint64(session.StreamID()))...), payload...)); err != nil {
					return err
				}
				if err = stream.Close(); err != nil {
					return err
				}
				select {
				case response := <-unis:
					id, err := readInteger(response)
					if err != nil {
						return err
					}
					if id != uint64(session.StreamID()) {
						return errors.New("cycle stream association")
					}
					reader = response
				case <-ctx.Done():
					return ctx.Err()
				}
			} else {
				stream, err := conn.OpenStreamSync(ctx)
				if err != nil {
					return err
				}
				if err = write(stream, append(append(integer(0x41), integer(uint64(session.StreamID()))...), payload...)); err != nil {
					return err
				}
				if err = stream.Close(); err != nil {
					return err
				}
				reader = stream
			}
			actual, err := io.ReadAll(reader)
			if err != nil {
				return err
			}
			if !bytes.Equal(actual, payload) {
				return errors.New("cycle echo differs")
			}
			sent += uint64(len(payload))
			consumed += uint64(len(actual))
			if index%4 == 3 {
				if err = write(session, frame(0, frame(0x190B4D3D, integer(consumed+65536)))); err != nil {
					return err
				}
			}
			if uni && index%4 == 3 {
				if err = write(session, frame(0, frame(0x190B4D40, integer(index+1+8)))); err != nil {
					return err
				}
			}
		}
	}
	datagram := append(integer(uint64(session.StreamID())/4), []byte("after-stream-cycles")...)
	if err := conn.SendDatagram(datagram); err != nil {
		return err
	}
	received, err := conn.ReceiveDatagram(ctx)
	if err != nil {
		return err
	}
	if !bytes.Equal(datagram, received) {
		return errors.New("cycle datagram differs")
	}
	if err := write(session, frame(0, frame(0x2843, []byte{0, 0, 0, 0}))); err != nil {
		return err
	}
	if err := session.Close(); err != nil {
		return err
	}
	_, err = io.ReadAll(session)
	return err
}

func main() {
	err := run()
	report := map[string]any{"passed": err == nil}
	if err != nil {
		report["error"] = err.Error()
	}
	json.NewEncoder(os.Stdout).Encode(report)
	if err != nil {
		os.Exit(1)
	}
}
