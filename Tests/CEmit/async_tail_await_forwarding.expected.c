// file: async_tail_await_forwarding.c
#include "async_tail_await_forwarding_private.h"

/* Private file declarations. */
static void inner(void (* complete)(void *context, int result), void *complete_context);
static void outer(void (* complete)(void *context, int result), void *complete_context);

static void inner(void (* complete)(void *context, int result), void *complete_context)
{
	complete(complete_context, 11);
	return;
}

static void outer(void (* complete)(void *context, int result), void *complete_context)
{
	inner(complete, complete_context);
	return;
}

// file: async_tail_await_forwarding_private.h
#ifndef ASYNC_TAIL_AWAIT_FORWARDING_PRIVATE_H_
#define ASYNC_TAIL_AWAIT_FORWARDING_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */

/* Object declarations. */


#endif
