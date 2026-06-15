// file: large_integer_literals.c
#include "large_integer_literals_private.h"


int64_t positiveLarge(void)
{
	return 4294967296LL;
}

uint64_t positiveLargeUnsigned(void)
{
	return 4294967296ULL;
}

int64_t negativeLarge(void)
{
	return -2147483649LL;
}

uint64_t hexLargeUnsigned(void)
{
	return 0x100000000ULL;
}

int64_t signedMinBoundary(void)
{
	return -2147483648;
}

uint64_t unsignedMaxBoundary(void)
{
	return 4294967295u;
}

// file: large_integer_literals.h
#ifndef LARGE_INTEGER_LITERALS_H_
#define LARGE_INTEGER_LITERALS_H_

#include "large_integer_literals_private.h"

int64_t positiveLarge(void);
uint64_t positiveLargeUnsigned(void);
int64_t negativeLarge(void);
uint64_t hexLargeUnsigned(void);
int64_t signedMinBoundary(void);
uint64_t unsignedMaxBoundary(void);

#endif
// file: large_integer_literals_private.h
#ifndef LARGE_INTEGER_LITERALS_PRIVATE_H_
#define LARGE_INTEGER_LITERALS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Newtypes. */

/* Enums. */

/* Layouts. */

/* Function declarations. */
int64_t positiveLarge(void);
uint64_t positiveLargeUnsigned(void);
int64_t negativeLarge(void);
uint64_t hexLargeUnsigned(void);
int64_t signedMinBoundary(void);
uint64_t unsignedMaxBoundary(void);

/* Object declarations. */


#endif
