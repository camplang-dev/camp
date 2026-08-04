// file: static_class_emission.c
#include "static_class_emission_private.h"

/* Private file declarations. */
static int Metrics_count;

static int Metrics_count;
int Metrics_getCount(void)
{
	return Metrics_count;
}

void Metrics_reset(void)
{
	Metrics_count = Metrics_Default;
}

void Metrics_add(int value)
{
	Metrics_count = (Metrics_count + value);
}

int main(void)
{
	Metrics_reset();
	Metrics_add(5);
	return Metrics_getCount();
}

// file: static_class_emission.h
#ifndef STATIC_CLASS_EMISSION_H_
#define STATIC_CLASS_EMISSION_H_

#include "static_class_emission_private.h"

void Metrics_reset(void);
void Metrics_add(int value);
int main(void);

#endif
// file: static_class_emission_private.h
#ifndef STATIC_CLASS_EMISSION_PRIVATE_H_
#define STATIC_CLASS_EMISSION_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Constants. */
#define Metrics_Default ((int)7)

/* Layouts. */

/* Function declarations. */
int Metrics_getCount(void);
void Metrics_reset(void);
void Metrics_add(int value);
int main(void);

/* Object declarations. */


#endif
