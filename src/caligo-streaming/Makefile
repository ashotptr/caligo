CC ?= gcc
OPTIMIZE ?= -O2 -g
WARNINGS = -std=c99 -Wall -Wextra -Wshadow -Wconversion -Wno-sign-conversion
CFLAGS = $(OPTIMIZE) $(WARNINGS) $(EXTRA)

BUILD ?= build

RELAY_MODULES = mp3 stream http ogg websocket objects qmux moqlite
VOD_MODULES = http media client
PACKAGER_MODULES = http media client ladder probe playlist
NETEM_MODULES = impair

RELAY_LIBS = -lpthread
VOD_LIBS = -lcrypto -ljansson -lpthread
PACKAGER_LIBS = -lcrypto -ljansson -lm

PICOQUIC ?= /opt/picoquic
PICOQUIC_BUILD ?= $(PICOQUIC)/build
PICOTLS ?= $(PICOQUIC_BUILD)/_deps/picotls-src
QUIC_CFLAGS = $(OPTIMIZE) -std=gnu11 -Wall -Wextra -Wshadow -Wconversion -Wno-sign-conversion $(EXTRA) \
	-isystem $(PICOQUIC)/picoquic -isystem $(PICOQUIC)/picohttp -isystem $(PICOTLS)/include
QUIC_LIBS = -L$(PICOQUIC_BUILD) -L$(PICOQUIC_BUILD)/_deps/picotls-build \
	-lpicohttp-core -lpicoquic-core -lpicotls-openssl -lpicotls-minicrypto -lpicotls-core -lssl -lcrypto -lpthread -lm

UNIT_TESTS = test_mp3 test_stream test_http test_ogg test_websocket test_objects test_moqlite test_media test_ladder test_probe test_playlist test_client test_impair

objects = $(1:%=$(BUILD)/%.o)

all: $(BUILD)/caligo-relay $(BUILD)/caligo-vod $(BUILD)/caligo-packager $(BUILD)/caligo-netem

relay: $(BUILD)/caligo-relay

relay-quic: $(BUILD)/caligo-relay-quic

vod: $(BUILD)/caligo-vod

packager: $(BUILD)/caligo-packager

netem: $(BUILD)/caligo-netem

$(BUILD):
	mkdir -p $(BUILD)

$(BUILD)/%.o: src/%.c $(wildcard src/*.h) | $(BUILD)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD)/caligo-relay: $(call objects,$(RELAY_MODULES) quic_off relay)
	$(CC) $(CFLAGS) -o $@ $^ $(RELAY_LIBS)

$(BUILD)/quic.o: src/quic.c $(wildcard src/*.h) | $(BUILD)
	$(CC) $(QUIC_CFLAGS) -c $< -o $@

$(BUILD)/caligo-relay-quic: $(call objects,$(RELAY_MODULES) quic relay)
	$(CC) $(CFLAGS) -o $@ $^ $(QUIC_LIBS)

$(BUILD)/caligo-vod: $(call objects,$(VOD_MODULES) vod)
	$(CC) $(CFLAGS) -o $@ $^ $(VOD_LIBS)

$(BUILD)/caligo-packager: $(call objects,$(PACKAGER_MODULES) packager)
	$(CC) $(CFLAGS) -o $@ $^ $(PACKAGER_LIBS)

$(BUILD)/caligo-netem: $(call objects,$(NETEM_MODULES) netem)
	$(CC) $(CFLAGS) -o $@ $^

$(BUILD)/test_mp3: tests/test_mp3.c tests/check.h $(call objects,mp3) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,mp3)

$(BUILD)/test_stream: tests/test_stream.c tests/check.h $(call objects,stream mp3) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,stream mp3)

$(BUILD)/test_http: tests/test_http.c tests/check.h $(call objects,http) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,http)

$(BUILD)/test_ogg: tests/test_ogg.c tests/check.h $(call objects,ogg) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,ogg)

$(BUILD)/test_websocket: tests/test_websocket.c tests/check.h $(call objects,websocket) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,websocket)

$(BUILD)/test_objects: tests/test_objects.c tests/check.h $(call objects,objects) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,objects)

$(BUILD)/test_moqlite: tests/test_moqlite.c tests/check.h $(call objects,moqlite qmux objects websocket) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,moqlite qmux objects websocket)

$(BUILD)/test_media: tests/test_media.c tests/check.h $(call objects,media) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,media) -lcrypto

$(BUILD)/test_ladder: tests/test_ladder.c tests/check.h $(call objects,ladder) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,ladder) -lm

$(BUILD)/test_probe: tests/test_probe.c tests/check.h $(call objects,probe) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,probe) -ljansson

$(BUILD)/test_playlist: tests/test_playlist.c tests/check.h $(call objects,playlist media) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,playlist media) -lcrypto -lm

$(BUILD)/test_client: tests/test_client.c tests/check.h $(call objects,client http) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,client http)

$(BUILD)/test_impair: tests/test_impair.c tests/check.h $(call objects,impair) | $(BUILD)
	$(CC) $(CFLAGS) -Isrc -o $@ $< $(call objects,impair)

unit: $(UNIT_TESTS:%=$(BUILD)/%)
	@for name in $(UNIT_TESTS); do ./$(BUILD)/$$name || exit 1; done

integration: all
	python3 tests/integration.py $(BUILD)/caligo-relay
	python3 tests/vod_integration.py $(BUILD)/caligo-vod
	python3 tests/packager_integration.py $(BUILD)/caligo-packager

interop: relay
	python3 tests/lite_interop.py $(BUILD)/caligo-relay

quic-test: relay-quic
	python3 tests/quic_integration.py $(BUILD)/caligo-relay-quic

browser-interop: relay-quic
	python3 tests/browser_interop.py $(BUILD)/caligo-relay-quic

netem-test: netem
	python3 tests/netem_integration.py $(BUILD)/caligo-netem

test: unit integration

sanitize:
	$(MAKE) test BUILD=build/sanitize OPTIMIZE="-O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer"

clean:
	rm -rf build

.PHONY: all relay relay-quic vod packager netem unit integration interop quic-test browser-interop netem-test test sanitize clean
