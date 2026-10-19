#!/bin/bash
set -u

PASSWORD="${LAB_SOURCE_PASSWORD:?set LAB_SOURCE_PASSWORD}"
RELAY="${LAB_RELAY:-relay:8000}"
ICECAST="${LAB_ICECAST:-icecast:8000}"
MEDIAMTX="${LAB_MEDIAMTX:-mediamtx}"
WEB=/lab-web
FILES=/lab-files
CONTROL=/lab-control

mkdir -p "$FILES" "$CONTROL"

if [ ! -s "$FILES/radio.mp3" ]; then
    ffmpeg -hide_banner -loglevel error -nostdin -f lavfi \
        -i "aevalsrc=exprs='0.08*sin(2*PI*330*t)+lt(mod(t\\,1)\\,0.06)*0.5*sin(PI*mod(t\\,1)/0.06)*sin(2*PI*(1000+200*mod(floor(t)\\,16))*t)':s=48000:c=stereo" \
        -t 600 -c:a libmp3lame -b:a 128k -f mp3 "$FILES/radio.mp3.part" && mv "$FILES/radio.mp3.part" "$FILES/radio.mp3"
fi

printf '{"clock":"realtime","codes":16,"base_hz":1000,"step_hz":200,"beep_ms":60,"sample_rate":48000,"reference":"%s"}\n' \
    "${LAB_REFERENCE:-}" > "$CONTROL/source.json.part" && mv "$CONTROL/source.json.part" "$CONTROL/source.json"

while true; do
    rm -rf "$WEB/hls" "$WEB/dash"
    mkdir -p "$WEB/hls" "$WEB/dash"

    outputs="[select=\\'a:0\\':f=mp3:content_type=audio/mpeg]icecast://source:$PASSWORD@$RELAY/live/"
    outputs+="|[select=\\'a:0\\':f=mp3:content_type=audio/mpeg]icecast://source:$PASSWORD@$ICECAST/stream"
    outputs+="|[select=\\'a:1\\':f=ogg:page_duration=20000:content_type=audio/ogg]icecast://source:$PASSWORD@$RELAY/live/opus"
    outputs+="|[select=\\'a:1\\':f=rtsp:rtsp_transport=tcp]rtsp://$MEDIAMTX:8554/radio"
    outputs+="|[select=\\'a:2\\':f=flv]rtmp://$MEDIAMTX:1935/radio-aac"
    outputs+="|[select=\\'a:2\\':f=hls:hls_time=2:hls_list_size=6:hls_flags=delete_segments+program_date_time]$WEB/hls/radio.m3u8"
    outputs+="|[select=\\'a:2\\':f=dash:seg_duration=2:window_size=6:extra_window_size=2:use_template=1:use_timeline=0]$WEB/dash/radio.mpd"

    python3 /usr/local/bin/lab-signal | ffmpeg -hide_banner -loglevel warning -nostdin \
        -f s16le -ar 48000 -ac 2 -i pipe:0 \
        -map 0:a -c:a:0 libmp3lame -b:a:0 128k \
        -map 0:a -c:a:1 libopus -b:a:1 96k -frame_duration:a:1 20 \
        -map 0:a -c:a:2 aac -b:a:2 128k \
        -f tee -use_fifo 1 \
        -fifo_options "attempt_recovery=1:recover_any_error=1:recovery_wait_time=1:drop_pkts_on_overflow=1:queue_size=256" \
        "$outputs"

    echo "lab-source: ffmpeg stopped, starting again in a second" >&2
    sleep 1
done
