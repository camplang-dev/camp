// file: fn_parameter_function_identity.c
#include "fn_parameter_function_identity_private.h"

/* Private file declarations. */
static void releaseThing(void *context);
static void add(void (* callback)(void *camp));

static void releaseThing(void *context)
{
}

static void add(void (* callback)(void *camp))
{
}

int main(void)
{
	add((void (*)(void *arg0))releaseThing);
	add((void (*)(void *arg0))releaseThing);
	return 0;
}

// file: fn_parameter_function_identity.h
#ifndef FN_PARAMETER_FUNCTION_IDENTITY_H_
#define FN_PARAMETER_FUNCTION_IDENTITY_H_

#include "fn_parameter_function_identity_private.h"

int main(void);

#endif
// file: fn_parameter_function_identity_private.h
#ifndef FN_PARAMETER_FUNCTION_IDENTITY_PRIVATE_H_
#define FN_PARAMETER_FUNCTION_IDENTITY_PRIVATE_H_

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
