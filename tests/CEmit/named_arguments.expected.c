// file: named_arguments.c
#include "named_arguments_private.h"

/* Private file declarations. */
static void record(int a, int b, int c);

static void record(int a, int b, int c)
{
}

int main(void)
{
	record(10, 2, 30);
	record(100, 2, 300);
	return 0;
}

// file: named_arguments.h
#ifndef NAMED_ARGUMENTS_H_
#define NAMED_ARGUMENTS_H_

#include "named_arguments_private.h"

int main(void);

#endif
// file: named_arguments_private.h
#ifndef NAMED_ARGUMENTS_PRIVATE_H_
#define NAMED_ARGUMENTS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int main(void);

/* Object declarations. */


#endif
