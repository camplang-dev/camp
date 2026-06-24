// file: default_named_constant_argument.c
#include "default_named_constant_argument_private.h"

/* Private file declarations. */
static int show(int command);
static const int DEFAULT_SHOW;

static const int DEFAULT_SHOW = 5;
static int show(int command)
{
	return command;
}

int run(void)
{
	return show(DEFAULT_SHOW);
}

// file: default_named_constant_argument.h
#ifndef DEFAULT_NAMED_CONSTANT_ARGUMENT_H_
#define DEFAULT_NAMED_CONSTANT_ARGUMENT_H_

#include "default_named_constant_argument_private.h"

int run(void);

#endif
// file: default_named_constant_argument_private.h
#ifndef DEFAULT_NAMED_CONSTANT_ARGUMENT_PRIVATE_H_
#define DEFAULT_NAMED_CONSTANT_ARGUMENT_PRIVATE_H_

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
