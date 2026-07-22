// file: default_arguments.c
#include "default_arguments_private.h"

/* Private file declarations. */
int externalDefault(int value);
static int addDefault(int left, int right);

static int addDefault(int left, int right)
{
	return (left + right);
}

int run(void)
{
	int total = addDefault(3, 5);
	total += externalDefault(7);
	return total;
}

// file: default_arguments.h
#ifndef DEFAULT_ARGUMENTS_H_
#define DEFAULT_ARGUMENTS_H_

#include "default_arguments_private.h"

int run(void);

#endif
// file: default_arguments_private.h
#ifndef DEFAULT_ARGUMENTS_PRIVATE_H_
#define DEFAULT_ARGUMENTS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int run(void);

/* Object declarations. */


#endif
