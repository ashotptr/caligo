#ifndef CALIGO_LADDER_H
#define CALIGO_LADDER_H

#define LADDER_MAX 12
#define LADDER_MIN_KBPS 100

typedef struct {
    int height;
    int kbps;
} Rung;

typedef struct {
    int width;
    int height;
    int kbps;
} Output;

int ladder_parse(const char *text, Rung *rungs, int capacity);

int ladder_plan(int source_width, int source_height, const Rung *rungs, int count, Output *outputs, int capacity);

int ladder_progressive(const Output *outputs, int count, int target_height);

#endif
