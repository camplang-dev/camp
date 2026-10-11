// file: erased_types_abi.c
#include "erased_types_abi_private.h"

/* Private file declarations. */
static int64_t Wide_get(Wide *this);
static void Wide_set(Wide *this, int64_t next);
static void main(void);

void *identity(void *p)
{
	return p;
}

const void *readonlyIdentity(const void *p)
{
	return p;
}

void **deeper(void **p)
{
	return p;
}

void array(void *values, uintptr_t values_length)
{
}

int input(const void *value)
{
	return (*(const int *)value);
}

void output(void *value)
{
	(*(int *)value) = 2;
}

void callback(void (* action)(const void *input, void *output))
{
}

static int64_t Wide_get(Wide *this)
{
	return this->value;
}

static void Wide_set(Wide *this, int64_t next)
{
	this->value = next;
}

int64_t carrierResult(int64_t value)
{
	return value;
}

void carrierCallback(int64_t (* action)(int64_t arg0))
{
}

static void main(void)
{
}

// file: erased_types_abi.h
#ifndef ERASED_TYPES_ABI_H_
#define ERASED_TYPES_ABI_H_

#include "erased_types_abi_private.h"

void *identity(void *p);
const void *readonlyIdentity(const void *p);
void **deeper(void **p);
void array(void *values, uintptr_t values_length);
int input(const void *value);
void output(void *value);
void callback(void (* action)(const void *input, void *output));
int64_t carrierResult(int64_t value);
void carrierCallback(int64_t (* action)(int64_t arg0));

#endif
// file: erased_types_abi_private.h
#ifndef ERASED_TYPES_ABI_PRIVATE_H_
#define ERASED_TYPES_ABI_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Point Point;
typedef struct Wide Wide;

/* Enums. */
typedef int Kind;
#define Kind_A ((Kind)0)
#define Kind_B ((Kind)1)

/* Newtypes. */

/* Layouts. */
struct Point
{
	int x;
};
struct Wide
{
	int64_t value;
};

/* Erased storage layouts. */
typedef struct { int value; bool specified; } __camp_erased_storage_117913a4a09d6895;

/* Function declarations. */
void *identity(void *p);
const void *readonlyIdentity(const void *p);
void **deeper(void **p);
void array(void *values, uintptr_t values_length);
int input(const void *value);
void output(void *value);
void callback(void (* action)(const void *input, void *output));
int64_t carrierResult(int64_t value);
void carrierCallback(int64_t (* action)(int64_t arg0));

/* Object declarations. */


#endif
