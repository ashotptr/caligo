#ifndef RELAY_CHECK_H
#define RELAY_CHECK_H

#include <stdio.h>
#include <string.h>

static int check_failures = 0;
static int check_count = 0;

#define CHECK(condition) \
    do { \
        check_count++; \
        if (!(condition)) { \
            check_failures++; \
            fprintf(stderr, "%s:%d: failed: %s\n", __FILE__, __LINE__, #condition); \
        } \
    } while (0)

#define CHECK_EQUAL(expected, actual) \
    do { \
        long long check_expected = (long long)(expected); \
        long long check_actual = (long long)(actual); \
        check_count++; \
        if (check_expected != check_actual) { \
            check_failures++; \
            fprintf(stderr, "%s:%d: %s is %lld, expected %lld\n", __FILE__, __LINE__, #actual, check_actual, check_expected); \
        } \
    } while (0)

#define CHECK_TEXT(expected, actual) \
    do { \
        check_count++; \
        if (strcmp((expected), (actual)) != 0) { \
            check_failures++; \
            fprintf(stderr, "%s:%d: %s is \"%s\", expected \"%s\"\n", __FILE__, __LINE__, #actual, (actual), (expected)); \
        } \
    } while (0)

#define CHECK_REPORT(name) \
    do { \
        printf("%s: %d checks, %d failed\n", (name), check_count, check_failures); \
        return check_failures == 0 ? 0 : 1; \
    } while (0)

#endif
