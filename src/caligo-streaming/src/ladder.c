#include "ladder.h"

#include <math.h>
#include <stdlib.h>

int ladder_parse(const char *text, Rung *rungs, int capacity) {
    int count = 0;
    const char *cursor = text;

    while (cursor && *cursor) {
        char *end = NULL;
        long height = strtol(cursor, &end, 10);

        if (end == cursor || *end != ':' || height < 16 || height > 8640) {
            return -1;
        }

        cursor = end + 1;

        long kbps = strtol(cursor, &end, 10);

        if (end == cursor || kbps < LADDER_MIN_KBPS || kbps > 200000 || (*end != ',' && *end != '\0')) {
            return -1;
        }

        if (count == capacity) {
            return -1;
        }

        rungs[count].height = (int)height;
        rungs[count].kbps = (int)kbps;
        count++;

        cursor = *end == ',' ? end + 1 : end;
    }

    for (int i = 1; i < count; i++) {
        Rung current = rungs[i];
        int j = i - 1;

        while (j >= 0 && rungs[j].height < current.height) {
            rungs[j + 1] = rungs[j];
            j--;
        }

        rungs[j + 1] = current;
    }

    return count > 0 ? count : -1;
}

static int even(double value) {
    long rounded = lround(value / 2.0) * 2;

    return rounded < 2 ? 2 : (int)rounded;
}

static int add_output(Output *outputs, int count, int capacity, int width, int height, int kbps) {
    for (int i = 0; i < count; i++) {
        if (outputs[i].width == width && outputs[i].height == height) {
            return count;
        }
    }

    if (count == capacity) {
        return count;
    }

    outputs[count].width = width;
    outputs[count].height = height;
    outputs[count].kbps = kbps < LADDER_MIN_KBPS ? LADDER_MIN_KBPS : kbps;

    return count + 1;
}

int ladder_plan(int source_width, int source_height, const Rung *rungs, int count, Output *outputs, int capacity) {
    if (source_width < 2 || source_height < 2 || count <= 0 || capacity <= 0) {
        return 0;
    }

    double source_long = source_width > source_height ? source_width : source_height;
    double source_short = source_width > source_height ? source_height : source_width;
    int native = -1;
    double native_box = 0;

    for (int i = 0; i < count; i++) {
        double box_short = rungs[i].height;
        double box_long = ceil(rungs[i].height * 16.0 / 9.0);
        double scale = fmin(box_long / source_long, box_short / source_short);

        if (scale >= 1.0) {
            native = i;
            native_box = box_long * box_short;
        }
    }

    int produced = 0;

    if (native >= 0) {
        double ratio = (source_long * source_short) / native_box;
        double kbps = rungs[native].kbps * fmin(1.0, pow(ratio, 0.75));

        produced = add_output(outputs, produced, capacity, source_width / 2 * 2, source_height / 2 * 2, (int)lround(kbps));
    }

    for (int i = 0; i < count; i++) {
        double box_short = rungs[i].height;
        double box_long = ceil(rungs[i].height * 16.0 / 9.0);
        double scale = fmin(box_long / source_long, box_short / source_short);

        if (scale < 1.0) {
            produced = add_output(outputs, produced, capacity, even(source_width * scale), even(source_height * scale), rungs[i].kbps);
        }
    }

    return produced;
}

int ladder_progressive(const Output *outputs, int count, int target_height) {
    int best = -1;

    for (int i = 0; i < count; i++) {
        int short_side = outputs[i].width < outputs[i].height ? outputs[i].width : outputs[i].height;

        if (short_side <= target_height && (best < 0 || outputs[i].kbps > outputs[best].kbps)) {
            best = i;
        }
    }

    if (best >= 0) {
        return best;
    }

    for (int i = 0; i < count; i++) {
        if (best < 0 || outputs[i].kbps < outputs[best].kbps) {
            best = i;
        }
    }

    return best;
}
